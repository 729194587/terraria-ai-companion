using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Linq;

namespace CompanionBridge.ServerIpc;

// Transport-independent. Only the simulation thread may call Handle/Clear.
internal sealed class AiReadOnlyProtocol
{
    internal sealed record Identity(object Actor, string World, long Generation, int Slot, object State, int SelectedSlot = -1, ulong GameUpdate = 0, bool CanAct = true);
    internal sealed record Binding(string actor, string world, long generation, int slot);
    private sealed record Lease(Identity Identity, Binding Binding);
    private readonly Dictionary<Guid, Lease> leases = new();
    private object actor;
    private string actorId;
    private readonly AiAtomicActions actions;
    internal AiReadOnlyProtocol(AiAtomicActions actions = null) { this.actions = actions; }
    internal void Clear() { leases.Clear(); actor = null; actorId = null; actions?.Clear(); }
    internal void Forget(Guid connection) { leases.Remove(connection); actions?.Forget(connection); }

    internal string Handle(Guid connection, string line, Func<Identity> capture, CancellationToken disconnected = default,
        Func<Identity,string,JsonElement,object> query = null)
    {
        string id = null;
        object Reply(bool ok, object data = null, Binding binding = null, string error = null) => new { v = 1, id, ok, binding, data, error };
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
                if (!names.Add(p.Name) || p.Name is not ("v" or "id" or "op" or "binding" or "actionId" or "operation" or "arguments" or "expiresAtUnixMs")) throw new FormatException();
            id = root.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || root.GetProperty("v").GetInt32() != 1) throw new FormatException();
            string op = root.GetProperty("op").GetString();
            bool execution = op is "start" or "action" or "cancel";
            bool reading = op == "query" && query != null;
            if (op is not ("bind" or "state") && !reading && (!execution || actions == null)) return JsonSerializer.Serialize(Reply(false, error: "unsupported_operation"));
            foreach (string field in names)
                if (field == "actionId" && !execution || field is "operation" or "arguments" && op != "start" && !reading
                    || field == "expiresAtUnixMs" && op != "start") throw new FormatException();
            if (reading && root.GetProperty("operation").GetString() is not ("get_player_state" or "inspect_inventory"))
                return JsonSerializer.Serialize(Reply(false, error: "unsupported_operation"));
            if (op == "bind" && root.TryGetProperty("binding", out _)) throw new FormatException();
            // Reject malformed bindings before invoking the engine adapter.
            Binding requested = null;
            if (op != "bind")
            {
                var b = root.GetProperty("binding");
                var fields = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in b.EnumerateObject())
                    if (!fields.Add(p.Name) || p.Name is not ("actor" or "world" or "generation" or "slot")) throw new FormatException();
                requested = new(b.GetProperty("actor").GetString(), b.GetProperty("world").GetString(),
                    b.GetProperty("generation").GetInt64(), b.GetProperty("slot").GetInt32());
            }
            Lease lease = null;
            if (op == "bind")
            {
                if (leases.ContainsKey(connection)) return JsonSerializer.Serialize(Reply(false, error: "already_bound"));
            }
            else
            {
                if (!leases.TryGetValue(connection, out lease)) return JsonSerializer.Serialize(Reply(false, error: "not_bound"));
                if (lease.Binding != requested) return JsonSerializer.Serialize(Reply(false, error: "stale_binding"));
            }
            Identity current;
            try { current = capture(); }
            catch { return JsonSerializer.Serialize(Reply(false, error: "engine_unavailable")); }
            if (current?.Actor == null || current.Generation <= 0 || current.Slot < 0 || string.IsNullOrEmpty(current.World) || current.World == "none")
                return JsonSerializer.Serialize(Reply(false, error: "identity_unavailable"));
            if (op == "bind")
            {
                if (!ReferenceEquals(actor, current.Actor)) { actor = current.Actor; actorId = Guid.NewGuid().ToString("N"); }
                var binding = new Binding(actorId, current.World, current.Generation, current.Slot);
                leases.Add(connection, new(current, binding));
                return JsonSerializer.Serialize(Reply(true, data: actions == null ? null : new { capabilities = query == null ? actions.Capabilities : actions.Capabilities.Append("server_ai_reads_v1").ToArray() }, binding: binding));
            }
            if (!ReferenceEquals(lease.Identity.Actor, current.Actor)
                || lease.Identity.World != current.World || lease.Identity.Generation != current.Generation || lease.Identity.Slot != current.Slot)
                return JsonSerializer.Serialize(Reply(false, error: "stale_binding"));
            if (reading)
            {
                try { return JsonSerializer.Serialize(Reply(true, query(current, root.GetProperty("operation").GetString(), root.GetProperty("arguments")), lease.Binding)); }
                catch (FormatException) { return JsonSerializer.Serialize(Reply(false, error: "invalid_request")); }
                catch { return JsonSerializer.Serialize(Reply(false, error: "engine_unavailable")); }
            }
            if (execution)
            {
                var reply = actions.Handle(connection, op, root, current, lease.Binding, disconnected);
                return JsonSerializer.Serialize(Reply(reply.Error == null, reply.Data, lease.Binding, reply.Error));
            }
            return JsonSerializer.Serialize(Reply(true, current.State, lease.Binding));
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { return JsonSerializer.Serialize(Reply(false, error: "invalid_request")); }
    }
}
