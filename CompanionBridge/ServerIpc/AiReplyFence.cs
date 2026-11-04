using System;
using System.Text.Json;

namespace CompanionBridge.ServerIpc;

// Connection-local transport metadata only. No Player/world access on workers.
// Advance and the actual native publication share this lock. Once Advance wins,
// even a previously queued say cannot enter the broadcaster.
internal sealed class AiReplyFence
{
    private readonly object gate=new();
    private long current;
    private bool closed;
    internal bool Advance(long turn)
    {
        lock(gate){if(closed||turn<=0||turn<current)return false;current=turn;return true;}
    }
    internal bool Publish(long turn,Action publish)
    {
        lock(gate){if(closed||turn<=0||turn!=current)return false;publish();return true;}
    }
    internal void Revoke(){lock(gate)closed=true;}
    internal void Observe(string line)
    {
        try
        {
            using var doc=JsonDocument.Parse(line,new JsonDocumentOptions{MaxDepth=8});var r=doc.RootElement;
            if(r.GetProperty("op").GetString()!="reply_turn"||r.GetProperty("v").GetInt32()!=1)return;
            var seen=new System.Collections.Generic.HashSet<string>();
            foreach(var p in r.EnumerateObject())if(!seen.Add(p.Name)||p.Name is not ("v" or "id" or "op" or "binding" or "replyTurn"))return;
            if(seen.Count!=5||string.IsNullOrWhiteSpace(r.GetProperty("id").GetString()))return;
            // The game-thread protocol still validates the lease and real actor
            // before acknowledging this turn. An invalid request may only fence
            // its own connection, never authorize publication or affect others.
            Advance(r.GetProperty("replyTurn").GetInt64());
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException or FormatException){ }
    }
}
