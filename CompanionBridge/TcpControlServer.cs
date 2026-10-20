using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CompanionBridge;

internal sealed class TcpControlServer : IDisposable
{
    internal const int DefaultPort = 18777;
    private readonly ControlState _state;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private Task _acceptTask, _clientTask;
    private CancellationTokenSource _clientCancellation;
    private int _session;
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    internal TcpControlServer(ControlState state, int port = DefaultPort)
    {
        _state = state;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Server.ExclusiveAddressUse = true;
    }

    internal void Start()
    {
        _listener.Start();
        _acceptTask = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                lock (_gate)
                {
                    if (_shutdown.IsCancellationRequested) { client.Dispose(); break; }
                    // Never replace an existing controller just because another process connects.
                    if (_clientCancellation != null) { client.Dispose(); continue; }
                    _session = _state.BeginSession();
                    _clientCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    int session = _session;
                    var cancellation = _clientCancellation;
                    _clientTask = Task.Run(() => Serve(client, session, cancellation));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task Serve(TcpClient client, int session, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var replies = Channel.CreateBounded<Task<ControlReply>>(ControlState.Capacity);
        Task writer = Task.CompletedTask;
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            await WriteJson(stream, new { v = 1, type = "hello", instance = _instance,
                lease_ms = ControlState.LeaseMilliseconds, capabilities = new[] { "guarded_control", "local_navigation" } }, token);
            writer = WriteReplies(stream, replies.Reader, token);
            var buffer = new byte[256];
            using var line = new MemoryStream();
            var utf8 = new UTF8Encoding(false, true);
            while (true)
            {
                int count = await stream.ReadAsync(buffer, token);
                if (count == 0) break;
                for (int i = 0; i < count; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        if (line.Length >= 1024) throw new InvalidDataException("Line too long.");
                        line.WriteByte(buffer[i]);
                        continue;
                    }
                    var request = ControlState.Parse(utf8.GetString(line.ToArray()));
                    line.SetLength(0);
                    var reply = _state.Enqueue(session, request, Stopwatch.GetTimestamp());
                    if (!replies.Writer.TryWrite(reply)) throw new InvalidDataException("Reply queue full.");
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException
            or JsonException or FormatException or InvalidOperationException or DecoderFallbackException) { }
        finally
        {
            _state.EndSession(session);
            cancellation.Cancel();
            replies.Writer.TryComplete();
            client.Dispose();
            try { await writer; }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            lock (_gate)
            {
                if (_session == session)
                {
                    _clientCancellation = null;
                    _session = 0;
                }
            }
            cancellation.Dispose();
        }
    }

    private static async Task WriteReplies(NetworkStream stream, ChannelReader<Task<ControlReply>> replies,
        CancellationToken token)
    {
        await foreach (var reply in replies.ReadAllAsync(token))
            await WriteJson(stream, await reply.WaitAsync(token), token);
    }

    private static async Task WriteJson(NetworkStream stream, object value, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + "\n");
        await stream.WriteAsync(bytes, token);
    }

    internal void Disconnect()
    {
        lock (_gate)
        {
            if (_clientCancellation == null) return;
            _state.EndSession(_session);
            _clientCancellation.Cancel();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Stop();
        Disconnect();
        Task accept = _acceptTask ?? Task.CompletedTask;
        Task client;
        lock (_gate) client = _clientTask ?? Task.CompletedTask;
        // Only used during unload; never wait for socket work in a game update hook.
        try { Task.WhenAll(accept, client).Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
    }
}
