using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompanionBridge;

internal enum AuthorityKind { Hello, Welcome, Arm, Armed, Execute, Result, Renew, Cancel }
internal sealed record AuthorityIdentity(Guid ClientWorld, Guid ServerWorld, int Session, int Generation, int ActionId, Guid Nonce);
internal sealed record AuthorityCell(int X, int Y, bool HasTile, int Type, int Wall, bool Half, int Slope, int FrameX, int FrameY, bool Actuated);
internal sealed record AuthorityIntent(string Operation, int X, int Y, int Slot, int ItemType, int Stack, int ChestBefore, AuthorityCell[] Cells);
internal sealed record NativeMutation(int Message, int Action, int X, int Y, int Data, int Style = 0);
internal sealed record AuthorityOutcome(string Status, string Reason, AuthorityCell[] Cells, int Stack, int ItemType, int Chest, ulong ServerUpdate);
internal sealed record AuthorityMessage(AuthorityKind Kind, Guid ClientWorld, Guid ServerWorld = default,
    AuthorityIdentity Identity = null, AuthorityIntent Intent = null, NativeMutation Native = null,
    int AfterStack = -1, AuthorityOutcome Outcome = null);

// Internal Mod wire format, independent of TCP/LLM parameter schemas. Bounded and
// strict: malformed packets never reach a native handler or create authority state.
internal static class AuthorityCodec
{
    internal const int MaxBytes = 8192;
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    internal static byte[] Encode(AuthorityMessage message)
    {
        Validate(message);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (bytes.Length > MaxBytes) throw new FormatException("Authority packet too large");
        return bytes;
    }
    internal static AuthorityMessage Decode(byte[] bytes)
    {
        if (bytes.Length is < 2 or > MaxBytes) throw new FormatException("Invalid authority packet length");
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        void Unique(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>();
                foreach (var p in element.EnumerateObject()) { if (!keys.Add(p.Name)) throw new FormatException("Duplicate authority field"); Unique(p.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
        }
        Unique(doc.RootElement);
        var message = JsonSerializer.Deserialize<AuthorityMessage>(bytes, Options);
        Validate(message); return message;
    }
    internal static void Validate(AuthorityMessage m)
    {
        if (m == null || !Enum.IsDefined(m.Kind) || m.ClientWorld == Guid.Empty) throw new FormatException("Invalid authority envelope");
        if (m.Kind is not (AuthorityKind.Hello or AuthorityKind.Welcome))
        {
            var id=m.Identity;
            if (id == null || id.ClientWorld!=m.ClientWorld || id.ServerWorld!=m.ServerWorld || id.ServerWorld==Guid.Empty
                || id.Session<=0 || id.Generation<0 || id.ActionId<=0 || id.Nonce==Guid.Empty) throw new FormatException("Invalid authority identity");
        }
        if (m.Kind==AuthorityKind.Welcome && m.ServerWorld==Guid.Empty) throw new FormatException("Missing server world");
        if (m.Kind==AuthorityKind.Arm)
        {
            var t=m.Intent;
            if(t==null || t.Operation is not ("place_wall" or "mine_wall" or "hammer_tile" or "interact_tile")
                || t.X is < 0 or > 100000 || t.Y is < 0 or > 100000 || t.Slot is < 0 or > 58
                || t.ItemType<0 || t.Stack is < 0 or > 999999 || t.ChestBefore is < -1 or > 7999) throw new FormatException("Invalid authority intent");
            Cells(t.Cells);
        }
        if(m.Kind==AuthorityKind.Execute && (m.Native==null || m.Native.Message is not (17 or 19 or 31 or 33 or 59)
            || m.Native.X is < 0 or > 100000 || m.Native.Y is < 0 or > 100000 || m.Native.Action is < -1 or > 255
            || m.Native.Data is < -1 or > 65535 || m.Native.Style is < 0 or > 255 || m.AfterStack is < -1 or > 999999)) throw new FormatException("Invalid native request");
        if(m.Kind==AuthorityKind.Result)
        {
            var r=m.Outcome;
            if(r==null || r.Status is not ("completed" or "failed" or "timeout" or "cancelled" or "unknown")
                || r.Reason==null || r.Reason.Length is < 1 or > 120 || r.Stack is < 0 or > 999999 || r.ItemType<0 || r.Chest is < -1 or > 7999) throw new FormatException("Invalid authority result");
            Cells(r.Cells);
        }
    }
    private static void Cells(AuthorityCell[] cells)
    {
        if(cells==null || cells.Length is < 1 or > 16 || cells.Any(c=>c==null) || cells.Select(c=>(c.X,c.Y)).Distinct().Count()!=cells.Length) throw new FormatException("Invalid authority cells");
        foreach(var c in cells) if(c.X is < 0 or > 100000 || c.Y is < 0 or > 100000 || c.Type is < 0 or > 65535
            || c.Wall is < 0 or > 65535 || c.Slope is < 0 or > 4 || c.FrameX is < -32768 or > 32767 || c.FrameY is < -32768 or > 32767) throw new FormatException("Invalid authority cell");
    }
}

internal interface IAtomicAuthorityWorld
{
    string Validate(int player, AuthorityIntent intent);
    AuthorityOutcome Execute(int player, AuthorityIntent intent, NativeMutation native, int afterStack);
    AuthorityOutcome Rejected(int player, AuthorityIntent intent, string status, string reason);
}

// One bounded slot per actual connection/player. Actor identity comes from the
// Mod packet sender, never the payload. No mutation is retried after dispatch.
internal sealed class AtomicAuthorityServer
{
    private sealed class Peer
    {
        internal Guid World;
        internal int LastAction;
        internal AuthorityIdentity Identity;
        internal AuthorityIntent Intent;
        internal long Lease, Deadline, Window;
        internal int Requests;
        internal AuthorityMessage Receipt;
    }
    private readonly Peer[] _peers = new Peer[255];
    internal Guid World { get; private set; } = Guid.NewGuid();
    internal void Reset() { World=Guid.NewGuid(); Array.Clear(_peers); }
    internal void Disconnect(int player) { if(player is >=0 and <255) _peers[player]=null; }
    internal AuthorityMessage Handle(int player, AuthorityMessage m, long nowMs, IAtomicAuthorityWorld world)
    {
        AuthorityCodec.Validate(m);
        if(player is <0 or >=255) return null;
        var peer=_peers[player] ??= new();
        if(nowMs-peer.Window>=1000) { peer.Window=nowMs; peer.Requests=0; }
        if(++peer.Requests>64) return null;
        if(m.Kind==AuthorityKind.Hello)
        {
            if(peer.World!=m.ClientWorld) { peer.World=m.ClientWorld; peer.Identity=null; peer.Intent=null; peer.Receipt=null; peer.LastAction=0; }
            return new(AuthorityKind.Welcome,m.ClientWorld,World);
        }
        if(m.ServerWorld!=World || peer.World!=m.ClientWorld) return null; // old connection/world: no side effects or misleading receipt
        AuthorityMessage Result(string status,string reason)
        {
            var outcome=world.Rejected(player,peer.Intent,status,reason);
            return peer.Receipt=new(AuthorityKind.Result,peer.World,World,peer.Identity,Intent:peer.Intent,Outcome:outcome);
        }
        if(m.Kind==AuthorityKind.Arm)
        {
            if(peer.Identity==m.Identity)
                return peer.Receipt ?? (nowMs<peer.Lease && nowMs<peer.Deadline ? new(AuthorityKind.Armed,peer.World,World,peer.Identity) : Result("timeout","server_lease_or_deadline_expired"));
            if(m.Identity.ActionId<=peer.LastAction) return null;
            peer.LastAction=m.Identity.ActionId; peer.Identity=m.Identity; peer.Intent=m.Intent; peer.Receipt=null;
            peer.Lease=nowMs+ControlState.LeaseMilliseconds; peer.Deadline=nowMs+10000;
            string reason;
            try { reason=world.Validate(player,peer.Intent); }
            catch(Exception) { return Result("unknown","server_validation_exception"); }
            return reason==null ? new(AuthorityKind.Armed,peer.World,World,peer.Identity) : Result("failed",reason);
        }
        if(peer.Identity!=m.Identity || peer.Intent==null) return null;
        if(peer.Receipt!=null) return peer.Receipt; // duplicate execute/cancel cannot repeat or undo effects
        if(m.Kind==AuthorityKind.Cancel) return Result("cancelled","client_cancelled_before_server_execution");
        if(nowMs>=peer.Lease || nowMs>=peer.Deadline) return Result("timeout","server_lease_or_deadline_expired");
        if(m.Kind==AuthorityKind.Renew) { peer.Lease=nowMs+ControlState.LeaseMilliseconds; return null; }
        if(m.Kind!=AuthorityKind.Execute) return null;
        string invalid;
        try { invalid=world.Validate(player,peer.Intent); }
        catch(Exception) { return Result("unknown","server_validation_exception"); }
        if(invalid!=null) return Result("failed",invalid);
        // Mark consumed before entering native/Mod hooks, including exception paths.
        Result("unknown","server_execution_in_progress");
        try { return peer.Receipt=new(AuthorityKind.Result,peer.World,World,peer.Identity,Intent:peer.Intent,Outcome:world.Execute(player,peer.Intent,m.Native,m.AfterStack)); }
        catch(Exception) { return Result("unknown","server_native_execution_exception"); }
    }
}
