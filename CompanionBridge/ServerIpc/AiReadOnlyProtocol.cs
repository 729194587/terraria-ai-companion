using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Linq;

namespace CompanionBridge.ServerIpc;

internal sealed class ShopReadException(string reason) : Exception(reason) { }
internal sealed class StaleNpcReferenceException : Exception { }

// Transport-independent. Only the simulation thread may call Handle/Clear.
internal sealed class AiReadOnlyProtocol
{
    internal sealed record Identity(object Actor, string World, long Generation, int Slot, object State, int SelectedSlot = -1, ulong GameUpdate = 0, bool CanAct = true)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        internal Lease RequestLease { get; init; }
    }
    internal sealed record Binding(string actor, string world, long generation, int slot);
    internal sealed record Lease(Identity Identity, Binding Binding)
    {
        // Engine payload is opaque to the transport. At most one quote per bound connection.
        internal object MerchantQuote;
        internal IDialogue Dialogue;
        internal void EndDialogue()
        {
            if (Dialogue == null) return;
            Dialogue.Dispose();
            Dialogue = null;
            MerchantQuote = null;
        }
    }
    internal interface IDialogue : IDisposable
    {
        bool IsCurrent { get; }
    }
    private readonly Dictionary<Guid, Lease> leases = new();
    private object actor;
    private string actorId;
    private readonly AiAtomicActions actions;
    private sealed record Speech(string Text, object Data, string Error);
    private readonly Dictionary<(Guid,string),Speech> speech = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid,AiReplyFence> replyFences=new();
    internal void ObserveReplyTurn(Guid connection,string line)=>replyFences.GetOrAdd(connection,_=>new()).Observe(line);
    internal void RevokeReplies(Guid connection){if(replyFences.TryGetValue(connection,out var f))f.Revoke();}
    internal AiReadOnlyProtocol(AiAtomicActions actions = null) { this.actions = actions; }
    internal string ActorId(Identity current)
    {
        if (!ReferenceEquals(actor, current.Actor)) { actor = current.Actor; actorId = Guid.NewGuid().ToString("N"); }
        return actorId;
    }
    internal void UpdateDialogues()
    {
        foreach (var lease in leases.Values)
            if (lease.Dialogue is { IsCurrent: false }) lease.EndDialogue();
    }
    internal void Clear() { foreach(var f in replyFences.Values)f.Revoke();replyFences.Clear();foreach (var lease in leases.Values) { lease.EndDialogue(); lease.MerchantQuote = null; } leases.Clear(); speech.Clear(); actor = null; actorId = null; actions?.Clear(); }
    internal void Forget(Guid connection) { RevokeReplies(connection);replyFences.TryRemove(connection,out _);if (leases.Remove(connection,out var lease)) { lease.EndDialogue(); lease.MerchantQuote = null; } foreach(var key in speech.Keys.Where(k=>k.Item1==connection).ToArray())speech.Remove(key); actions?.Forget(connection); }

    internal string Handle(Guid connection, string line, Func<Identity> capture, CancellationToken disconnected = default,
        Func<Identity,string,JsonElement,object> query = null, Func<Identity,string,object> say = null)
    {
        string id = null;
        object Reply(bool ok, object data = null, Binding binding = null, string error = null, string dispatchState = null) => new { v = 1, id, ok, binding, data, error, dispatchState };
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
                if (!names.Add(p.Name) || p.Name is not ("v" or "id" or "op" or "binding" or "actionId" or "operation" or "arguments" or "expiresAtUnixMs" or "text" or "messageId" or "replyTurn")) throw new FormatException();
            id = root.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || root.GetProperty("v").GetInt32() != 1) throw new FormatException();
            string op = root.GetProperty("op").GetString();
            bool execution = op is "start" or "action" or "cancel" or "retreat";
            bool reading = op == "query" && query != null;
            bool speaking = op=="say" && say!=null;
            bool turning=op=="reply_turn"&&say!=null;
            if (op is not ("bind" or "state") && !reading && !speaking && !turning && (!execution || actions == null)) return JsonSerializer.Serialize(Reply(false, error: "unsupported_operation"));
            foreach (string field in names)
                if (field == "actionId" && !execution || field=="operation" && op!="start" && !reading || field=="arguments" && op is not ("start" or "retreat") && !reading
                    || field == "expiresAtUnixMs" && op is not ("start" or "say")
                    || field is "text" or "messageId" && !speaking || field=="replyTurn"&&!speaking&&!turning) throw new FormatException();
            long replyTurn=0;
            if(speaking||turning){replyTurn=root.GetProperty("replyTurn").GetInt64();if(replyTurn<=0)throw new FormatException();}
            string messageId=null,text=null;
            if(speaking)
            {
                messageId=root.GetProperty("messageId").GetString();text=root.GetProperty("text").GetString();
                if(!Guid.TryParseExact(messageId,"N",out _)||string.IsNullOrWhiteSpace(text)||text.Length>400
                    ||text.IndexOfAny(new[]{'\r','\n','\0'})>=0)throw new FormatException();
            }
            if (reading && root.GetProperty("operation").GetString() is not ("get_player_state" or "inspect_inventory" or "observe_terrain" or "scan_tiles" or "scan_entities" or "inspect_tile" or "query_item_sources" or "query_item_info" or "resolve_content" or "observe_scene" or "query_recipes" or "query_map" or "query_combat" or "query_combat_loadout" or "list_pylons" or "list_teammates" or "discover_bosses" or "inspect_container" or "build_diff" or "inspect_housing" or "inspect_npc_housing" or "inspect_npc_shop" or "quote_npc_purchase" or "quote_npc_sale" or "quote_nurse_heal" or "quote_reforge"))
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
                var binding = new Binding(ActorId(current), current.World, current.Generation, current.Slot);
                leases.Add(connection, new(current, binding));
                var capabilities=actions?.Capabilities.ToList();
                if(query!=null){capabilities ??= new(); capabilities.Add("server_ai_item_sources_v1");capabilities.Add("server_ai_item_info_v1");capabilities.Add("server_ai_build_diff_v1");capabilities.Add("server_ai_housing_inspect_v1");capabilities.Add("server_ai_npc_housing_v1");if (actions?.SupportsMerchantShop == true) capabilities.Add("server_ai_merchant_shop_v1");capabilities?.Add("server_ai_reads_v1");capabilities?.Add("server_ai_perception_v1");capabilities?.Add("server_ai_scene_v1");capabilities?.Add("server_ai_scene_region_v1");capabilities?.Add("server_ai_map_v1");}
                if(say!=null)capabilities?.Add("server_ai_chat_v2");
                return JsonSerializer.Serialize(Reply(true, data: capabilities==null?null:new { capabilities, compatibility = actions?.Compatibility }, binding: binding));
            }
            if (!ReferenceEquals(lease.Identity.Actor, current.Actor)
                || lease.Identity.World != current.World || lease.Identity.Generation != current.Generation || lease.Identity.Slot != current.Slot)
                return JsonSerializer.Serialize(Reply(false, error: "stale_binding"));
            current = current with { RequestLease = lease };
            var replyFence=replyFences.GetOrAdd(connection,_=>new());
            if(turning)
                return JsonSerializer.Serialize(replyFence.Advance(replyTurn)?Reply(true,new{replyTurn},lease.Binding):Reply(false,error:"superseded"));
            if(speaking)
            {
                if(disconnected.IsCancellationRequested)return JsonSerializer.Serialize(Reply(false,error:"connection_cancelled"));
                long remaining=root.GetProperty("expiresAtUnixMs").GetInt64()-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if(remaining<=0||remaining>2000)return JsonSerializer.Serialize(Reply(false,error:remaining<=0?"request_expired":"invalid_deadline"));
                var key=(connection,messageId);Speech result=null;
                if(!replyFence.Publish(replyTurn,()=>{
                    if(speech.TryGetValue(key,out var cached)){result=cached.Text==text?cached:new(text,null,"duplicate_message");return;}
                    if(speech.Count>=1024){result=new(text,null,"reply_registry_full");return;}
                    // Fence uncertainty before entering the broadcaster; never retry.
                    speech[key]=new(text,null,"reply_outcome_unknown");
                    try{speech[key]=new(text,say(current,text),null);}catch{}
                    result=speech[key];
                }))return JsonSerializer.Serialize(Reply(false,error:"superseded"));
                return JsonSerializer.Serialize(Reply(result.Error==null,result.Data,lease.Binding,result.Error));
            }
            if (reading)
            {
                var arguments = root.GetProperty("arguments");
                if (arguments.ValueKind != JsonValueKind.Object) throw new FormatException();
                if (root.GetProperty("operation").GetString() == "inspect_container" && actions?.SupportsContainers != true)
                    return JsonSerializer.Serialize(Reply(false, error: "unsupported_operation"));
                if (root.GetProperty("operation").GetString() == "quote_reforge" && actions?.Capabilities.Contains("server_ai_reforge_v1") != true)
                    return JsonSerializer.Serialize(Reply(false,error:"unsupported_operation"));
                if (root.GetProperty("operation").GetString() == "quote_nurse_heal" && actions?.Capabilities.Contains("server_ai_nurse_v1") != true)
                    return JsonSerializer.Serialize(Reply(false,error:"unsupported_operation"));
                if (root.GetProperty("operation").GetString() == "quote_npc_sale" && actions?.Capabilities.Contains("server_ai_merchant_sale_v1") != true)
                    return JsonSerializer.Serialize(Reply(false, error: "unsupported_operation"));
                if (root.GetProperty("operation").GetString() is ("inspect_npc_shop" or "quote_npc_purchase") && actions?.SupportsMerchantShop != true)
                    return JsonSerializer.Serialize(Reply(false, error: "unsupported_operation"));
                if(root.GetProperty("operation").GetString()=="query_recipes" && actions?.SupportsCrafting!=true)return JsonSerializer.Serialize(Reply(false,error:"unsupported_operation"));
                if(root.GetProperty("operation").GetString()=="list_pylons" && actions?.Capabilities.Contains("server_ai_pylons_v1")!=true)return JsonSerializer.Serialize(Reply(false,error:"unsupported_operation"));
                if(root.GetProperty("operation").GetString()=="list_teammates" && actions?.Capabilities.Contains("server_ai_regroup_v1")!=true)return JsonSerializer.Serialize(Reply(false,error:"unsupported_operation"));
                if(root.GetProperty("operation").GetString()=="discover_bosses" && actions?.Capabilities.Contains("server_ai_boss_strategy_v1")!=true)return JsonSerializer.Serialize(Reply(false,error:"unsupported_operation"));
                if(root.GetProperty("operation").GetString()=="query_combat")
                {
                    if(actions?.SupportsCombat!=true)return JsonSerializer.Serialize(Reply(false,error:"unsupported_operation"));
                    if(arguments.EnumerateObject().Any())throw new FormatException();
                    return JsonSerializer.Serialize(Reply(true,new{available=true,worldSessionId=current.World,actorGeneration=current.Generation,
                        actorSlot=current.Slot,gameUpdate=current.GameUpdate,observedAtUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        clock="server_utc_at_projection",data=actions.CombatState(current)},lease.Binding));
                }
                try { return JsonSerializer.Serialize(Reply(true, query(current, root.GetProperty("operation").GetString(), arguments), lease.Binding)); }
                catch (ShopReadException e) { return JsonSerializer.Serialize(Reply(false, binding: lease.Binding, error: e.Message)); }
                catch (StaleNpcReferenceException) { return JsonSerializer.Serialize(Reply(false, binding: lease.Binding, error: "stale_entity_ref")); }
                catch (AtomicParameterException) { return JsonSerializer.Serialize(Reply(false, binding: lease.Binding, error: "invalid_request")); }
                catch { return JsonSerializer.Serialize(Reply(false, error: "engine_unavailable")); }
            }
            if (execution)
            {
                var reply = actions.Handle(connection, op, root, current, lease.Binding, disconnected);
                return JsonSerializer.Serialize(Reply(reply.Error == null, reply.Data, lease.Binding, reply.Error,
                    op == "start" ? reply.DispatchState ?? (reply.Error == null ? "admitted" : "not_admitted") : null));
            }
            return JsonSerializer.Serialize(Reply(true, current.State, lease.Binding));
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { return JsonSerializer.Serialize(Reply(false, error: "invalid_request")); }
    }
}
