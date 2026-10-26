using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CompanionBridge.ServerIpc;

// Transport-independent. Only the simulation thread may call Handle/Clear.
internal sealed class AiReadOnlyProtocol
{
    internal sealed record Identity(object Actor, string World, long Generation, int Slot, object State);
    internal sealed record Binding(string actor, string world, long generation, int slot);
    private sealed record Lease(Identity Identity, Binding Binding);
    private readonly Dictionary<Guid, Lease> leases = new();
    private object actor;
    private string actorId;
    internal void Clear() { leases.Clear(); actor = null; actorId = null; }
    internal void Forget(Guid connection) => leases.Remove(connection);

    internal string Handle(Guid connection, string line, Func<Identity> capture)
    {
        string id = null;
        object Reply(bool ok, object data = null, Binding binding = null, string error = null) => new { v = 1, id, ok, binding, data, error };
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
                if (!names.Add(p.Name) || p.Name is not ("v" or "id" or "op" or "binding")) throw new FormatException();
            id = root.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || root.GetProperty("v").GetInt32() != 1) throw new FormatException();
            string op = root.GetProperty("op").GetString();
            if (op is not ("bind" or "state")) return JsonSerializer.Serialize(Reply(false, error: "unsupported_operation"));
            if (op == "bind" && root.TryGetProperty("binding", out _)) throw new FormatException();
            // Reject malformed bindings before invoking the engine adapter.
            Binding requested = null;
            if (op == "state")
            {
                var b = root.GetProperty("binding");
                var fields = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in b.EnumerateObject())
                    if (!fields.Add(p.Name) || p.Name is not ("actor" or "world" or "generation" or "slot")) throw new FormatException();
                requested = new(b.GetProperty("actor").GetString(), b.GetProperty("world").GetString(),
                    b.GetProperty("generation").GetInt64(), b.GetProperty("slot").GetInt32());
            }
            Identity current;
            try { current = capture(); }
            catch { return JsonSerializer.Serialize(Reply(false, error: "engine_unavailable")); }
            if (current?.Actor == null || current.Generation <= 0 || current.Slot < 0 || string.IsNullOrEmpty(current.World) || current.World == "none")
                return JsonSerializer.Serialize(Reply(false, error: "identity_unavailable"));
            if (op == "bind")
            {
                if (leases.ContainsKey(connection)) return JsonSerializer.Serialize(Reply(false, error: "already_bound"));
                if (!ReferenceEquals(actor, current.Actor)) { actor = current.Actor; actorId = Guid.NewGuid().ToString("N"); }
                var binding = new Binding(actorId, current.World, current.Generation, current.Slot);
                leases.Add(connection, new(current, binding));
                return JsonSerializer.Serialize(Reply(true, binding: binding));
            }
            if (!leases.TryGetValue(connection, out var lease)) return JsonSerializer.Serialize(Reply(false, error: "not_bound"));
            if (lease.Binding != requested || !ReferenceEquals(lease.Identity.Actor, current.Actor)
                || lease.Identity.World != current.World || lease.Identity.Generation != current.Generation || lease.Identity.Slot != current.Slot)
                return JsonSerializer.Serialize(Reply(false, error: "stale_binding"));
            return JsonSerializer.Serialize(Reply(true, current.State, lease.Binding));
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { return JsonSerializer.Serialize(Reply(false, error: "invalid_request")); }
    }
}
