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
    internal const int MaxResponseBytes = 16 * 1024 * 1024; // UTF-8 including newline
    internal const int MaxRequestBytes = 4096;
    internal const int DefaultPort = 18777;
    internal static int ParseLaunchPort(string value)
    {
        if (value == null) return DefaultPort;
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port)
            || port is < 1024 or > 65535)
            throw new FormatException("-companionbridgeport must be an integer in 1024..65535");
        return port;
    }
    private readonly ControlState _state;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private Task _acceptTask, _clientTask;
    private CancellationTokenSource _clientCancellation;
    private int _session;
    private Channel<object> _events;
    private Channel<object> _gameEvents;
    private int _gameMessagesDropped;
    private long _chatSequence;

    internal void PublishChat(int sender, string name, string text, int localPlayer)
    {
        // Author 255 is system text; local-player echoes must never trigger the Agent.
        if (sender < 0 || sender >= 255 || sender == localPlayer || string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            if (_events == null || _clientCancellation == null) return;
            string id = _instance + ":" + Interlocked.Increment(ref _chatSequence);
            _state.ObserveChat(id);
            if (!_events.Writer.TryWrite(new { v = 1, type = "chat_message", eventId = id,
                senderId = sender, senderName = name, text,
                receivedAt = DateTimeOffset.UtcNow.ToString("O"), isSelf = false }))
                Disconnect(); // Explicit failure instead of silently losing messages.
        }
    }
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    internal void PublishGameMessage(string text, string source, ulong gameUpdate)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            if (_gameEvents == null || _clientCancellation == null) return;
            string id = _instance + ":" + Interlocked.Increment(ref _chatSequence);
            // Separate bounded queue: notice bursts cannot fill the player-chat queue or revoke chatId.
            if (_gameEvents.Writer.TryWrite(new { v = 1, type = "game_message", eventId = id,
                text = text.Length > 1024 ? text[..1024] : text, textTruncated = text.Length > 1024,
                source, gameUpdate, receivedAt = DateTimeOffset.UtcNow.ToString("O"),
                droppedBefore = _gameMessagesDropped })) _gameMessagesDropped = 0;
            else _gameMessagesDropped++;
        }
    }

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
                    _events = Channel.CreateBounded<object>(ControlState.Capacity);
                    _gameEvents = Channel.CreateBounded<object>(ControlState.Capacity);
                    _gameMessagesDropped = 0;
                    int session = _session;
                    var cancellation = _clientCancellation;
                    var events = _events;
                    var gameEvents = _gameEvents;
                    _clientTask = Task.Run(() => Serve(client, session, cancellation, events, gameEvents));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task Serve(TcpClient client, int session, CancellationTokenSource cancellation, Channel<object> events,
        Channel<object> gameEvents)
    {
        var token = cancellation.Token;
        var replies = Channel.CreateBounded<Task<ControlReply>>(ControlState.Capacity);
        Task writer = Task.CompletedTask;
        Task eventWriter = Task.CompletedTask;
        Task gameEventWriter = Task.CompletedTask;
        using var writeGate = new SemaphoreSlim(1, 1);
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            await WriteJson(stream, new { v = 1, type = "hello", instance = _instance,
                lease_ms = ControlState.LeaseMilliseconds, capabilities = new[] { "local_terminal_input_v1", "server_confirmed_interactions_v1", "atomic_operations_v1", "atomic_extensions_v1", "atomic_foundations_v1", "inventory_operations_v1", "hotbar_keys_v1", "platform_drop_v1", "landing_region_navigation_v1", "guarded_control", "local_navigation", "bounded_movement_v1", "movement_loop_v1", "chat_events", "game_messages_v1", "send_chat", "nearby_entities", "viewport_observation_v1" }, maxResponseBytes = MaxResponseBytes }, writeGate, token);
            writer = WriteReplies(stream, replies.Reader, writeGate, token);
            eventWriter = WriteEvents(stream, events.Reader, writeGate, token);
            gameEventWriter = WriteEvents(stream, gameEvents.Reader, writeGate, token);
            // A failed writer must wake the reader and revoke the session as well.
            _ = Task.WhenAny(writer, eventWriter, gameEventWriter).ContinueWith(_ => cancellation.Cancel(),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
                        if (line.Length >= MaxRequestBytes) throw new InvalidDataException("Line too long.");
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
            events.Writer.TryComplete();
            gameEvents.Writer.TryComplete();
            client.Dispose();
            try { await Task.WhenAll(writer, eventWriter, gameEventWriter); }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            lock (_gate)
            {
                if (_session == session)
                {
                    _clientCancellation = null;
                    _session = 0;
                    _events = null;
                    _gameEvents = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private static async Task WriteReplies(NetworkStream stream, ChannelReader<Task<ControlReply>> replies,
        SemaphoreSlim gate, CancellationToken token)
    {
        await foreach (var reply in replies.ReadAllAsync(token))
            await WriteJson(stream, await reply.WaitAsync(token), gate, token);
    }

    private static async Task WriteEvents(NetworkStream stream, ChannelReader<object> events,
        SemaphoreSlim gate, CancellationToken token)
    {
        await foreach (var value in events.ReadAllAsync(token))
            await WriteJson(stream, value, gate, token);
    }

    private static async Task WriteJson(NetworkStream stream, object value, SemaphoreSlim gate, CancellationToken token)
    {
        byte[] bytes = SerializeFrame(value);
        await gate.WaitAsync(token);
        try { await stream.WriteAsync(bytes, token); }
        finally { gate.Release(); }
    }

    // Called off the game thread. Serialize the snapshot once, then append a small metrics member.
    internal static byte[] SerializeFrame(object value)
    {
        long started = Stopwatch.GetTimestamp();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        byte[] metrics = null;
        if (value is ControlReply reply && reply.Observation != null)
        {
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            metrics = JsonSerializer.SerializeToUtf8Bytes(new {
                transportMetrics = new { measuredPayloadBytes = bytes.Length + 1, serializationMilliseconds = elapsed,
                    measurement = "first full reply serialization; excludes this metrics envelope and network write" }
            }, JsonOptions);
        }
        int length = bytes.Length + (metrics?.Length ?? 1);
        if (length > MaxResponseBytes)
        {
            if (value is not ControlReply oversized) throw new InvalidDataException("response_too_large");
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
                v = 1, oversized.Id, ok = false, status = "response_too_large", oversized.Mode,
                requiredBytes = length, maxResponseBytes = MaxResponseBytes
            }, JsonOptions) + "\n");
        }
        var frame = new byte[length];
        if (metrics == null) Buffer.BlockCopy(bytes, 0, frame, 0, bytes.Length);
        else
        {
            Buffer.BlockCopy(bytes, 0, frame, 0, bytes.Length - 1);
            frame[bytes.Length - 1] = (byte)',';
            Buffer.BlockCopy(metrics, 1, frame, bytes.Length, metrics.Length - 1);
        }
        frame[^1] = (byte)'\n';
        return frame;
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
