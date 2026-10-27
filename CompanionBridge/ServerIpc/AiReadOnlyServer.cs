using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace CompanionBridge.ServerIpc;

// No Terraria references: workers exchange bounded text/jobs only.
internal sealed class AiReadOnlyServer : IDisposable
{
    internal const int DefaultPort = 18779;
    internal static int ParsePort(string value, int humanPort)
    {
        int port = value == null ? DefaultPort : int.TryParse(value, out int n) ? n : -1;
        if (port < 1024 || port > 65535 || port == humanPort) throw new ArgumentException("AI IPC port must be 1024..65535 and distinct from the human TCP port.");
        return port;
    }
    private sealed record Job(Guid Connection, string Line, CancellationToken Token, long Deadline, TaskCompletionSource<string> Reply);
    private readonly ConcurrentQueue<Job> jobs = new();
    private readonly ConcurrentQueue<Guid> closed = new();
    private readonly CancellationTokenSource stop = new();
    private readonly TcpListener listener;
    private readonly AiReadOnlyProtocol protocol;
    private readonly AiAtomicActions actions;
    private int queued, clients;
    internal int PendingCount => Volatile.Read(ref queued);
    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    private readonly int ownerThread;
    internal AiReadOnlyServer(int port, AiAtomicActions actions = null) { this.actions = actions; protocol = new(actions); listener = new(IPAddress.Loopback, port); listener.ExclusiveAddressUse = true; ownerThread = Environment.CurrentManagedThreadId; }
    internal void Start() { listener.Start(4); _ = Accept(); }
    private async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                if (Interlocked.Increment(ref clients) > 4) { Interlocked.Decrement(ref clients); client.Dispose(); continue; }
                _ = Serve(client);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task Serve(TcpClient client)
    {
        Guid connection = Guid.NewGuid();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var token = cancellation.Token;
        var replies = System.Threading.Channels.Channel.CreateBounded<Task<string>>(16);
        async Task Write()
        {
            try
            {
                await foreach (var pending in replies.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    string reply = await pending.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    byte[] bytes = Encoding.UTF8.GetBytes(reply + "\n");
                    await client.GetStream().WriteAsync(bytes, token).ConfigureAwait(false);
                }
            }
            finally { cancellation.Cancel(); client.Dispose(); }
        }
        Task writer = Write();
        try
        {
            var stream = client.GetStream();
            var line = new MemoryStream();
            byte[] buffer = new byte[1];
            var utf8 = new UTF8Encoding(false, true);
            while (await stream.ReadAsync(buffer, token).ConfigureAwait(false) != 0)
            {
                if (buffer[0] != 10) { if (line.Length >= 4096) break; line.WriteByte(buffer[0]); continue; }
                string text = utf8.GetString(line.ToArray()); line.SetLength(0);
                if (Interlocked.Increment(ref queued) > 64) { Interlocked.Decrement(ref queued); break; }
                var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                jobs.Enqueue(new(connection, text, token, Environment.TickCount64 + 4000, reply));
                if (!replies.Writer.TryWrite(reply.Task)) break;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or DecoderFallbackException) { }
        finally
        {
            cancellation.Cancel(); client.Dispose(); replies.Writer.TryComplete();
            try { await writer.ConfigureAwait(false); } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or TimeoutException) { }
            closed.Enqueue(connection); Interlocked.Decrement(ref clients);
        }
    }
    internal void Tick(Func<AiReadOnlyProtocol.Identity> capture, Action<AiReadOnlyProtocol.Identity, int> select = null,
        Func<AiReadOnlyProtocol.Identity,string,JsonElement,object> query = null,
        Func<AiReadOnlyProtocol.Identity,string,object> say = null)
    {
        AssertOwner();
        while (closed.TryDequeue(out var connection)) protocol.Forget(connection);
        actions?.Tick(capture, select);
        // Bound work per simulation tick; workers never invoke capture.
        for (int i = 0; i < 16 && jobs.TryDequeue(out var job); i++)
        {
            Interlocked.Decrement(ref queued);
            if (job.Token.IsCancellationRequested || stop.IsCancellationRequested || Environment.TickCount64 >= job.Deadline)
            { job.Reply.TrySetCanceled(); continue; }
            try { job.Reply.TrySetResult(protocol.Handle(job.Connection, job.Line, capture, job.Token, query, say)); }
            catch { job.Reply.TrySetResult("{\"v\":1,\"ok\":false,\"error\":\"engine_unavailable\"}"); }
        }
    }
    private void AssertOwner() { if (ownerThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("IPC dispatch requires the simulation thread."); }
    public void Dispose()
    {
        AssertOwner(); stop.Cancel(); listener.Stop();
        while (jobs.TryDequeue(out var job)) { Interlocked.Decrement(ref queued); job.Reply.TrySetCanceled(); }
        protocol.Clear();
    }
}
