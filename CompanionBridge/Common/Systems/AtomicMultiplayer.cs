using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace CompanionBridge.Common.Systems;

// Correlates only Agent-generated native requests. It never sends an untagged
// duplicate of a captured mutation, and never edits client tile data for success.
public sealed class AtomicMultiplayer : ModSystem
{
    private static readonly ConcurrentQueue<(int Sender, AuthorityMessage Message)> Incoming = new();
    private static readonly AtomicAuthorityServer Authority = new();
    private static readonly NativeAuthorityWorld World = new();
    private static Guid _serverWorld;
    private sealed class ClientAction
    {
        internal AuthorityIdentity Id;
        internal AuthorityIntent Intent;
        internal bool Armed, Sent, CancelSent;
        internal NativeMutation Native;
        internal AuthorityOutcome Outcome;
        internal string Error;
        internal long Started, Renewed, Received;
    }
    private static ClientAction _client;
    private static bool _inputScope;
    private static long Now => Stopwatch.GetTimestamp()*1000/Stopwatch.Frequency;
    internal static bool Required(string operation) => operation is "mine_wall" or "place_wall" or "hammer_tile" or "interact_tile";
    private static bool Current(ClientAction c) => c!=null && CompanionBridge.Controls.AtomicAction is { Status:"running" } a
        && a.ActionId==c.Id.ActionId && a.Session==c.Id.Session && a.Generation==c.Id.Generation
        && a.WorldSessionId==c.Id.ClientWorld.ToString("N");
    internal static void BeginInput() { _inputScope=Main.netMode==NetmodeID.MultiplayerClient && Current(_client) && _client.Armed && !_client.Sent; }
    internal static void EndInput() { _inputScope=false; }

    public override void Load() => On_NetMessage.SendData += Capture;
    public override void Unload() { On_NetMessage.SendData -= Capture; Reset(); }
    public override void OnWorldLoad() => Reset();
    public override void OnWorldUnload() => Reset();
    private static void Reset()
    {
        _client=null; _serverWorld=Guid.Empty; _inputScope=false; Authority.Reset();
        while(Incoming.TryDequeue(out _)) { }
    }
    internal static void Disconnect(int player) => Authority.Disconnect(player);
    internal static void Receive(BinaryReader reader,int sender,Action<BinaryReader,int> spectator = null)
    {
        try
        {
            int length=reader.ReadUInt16();
            if(length == ushort.MaxValue) { spectator?.Invoke(reader, sender); return; }
            if(length is <2 or >AuthorityCodec.MaxBytes || Incoming.Count>=256) return;
            byte[] bytes=reader.ReadBytes(length);
            if(bytes.Length!=length) return;
            Incoming.Enqueue((sender,AuthorityCodec.Decode(bytes)));
        }
        catch(Exception e) when(e is IOException or FormatException or System.Text.Json.JsonException or ArgumentException) { }
    }
    private static void Send(AuthorityMessage message,int target=-1)
    {
        byte[] bytes=AuthorityCodec.Encode(message);
        var packet=ModContent.GetInstance<CompanionBridge>().GetPacket(bytes.Length+2);
        packet.Write((ushort)bytes.Length); packet.Write(bytes); packet.Send(target);
    }
    private static void Arm(ClientAction c)
    {
        c.Id=c.Id with { ServerWorld=_serverWorld };
        // Terraria's inventory is normally client-synchronized. Establish the
        // selected server stack before arming; subsequent selected-slot sync is
        // held until the server consumes/returns the authoritative stack.
        NetMessage.SendData(13,number:Main.myPlayer);
        NetMessage.SendData(5,number:Main.myPlayer,number2:c.Intent.Slot);
        Send(new(AuthorityKind.Arm,c.Id.ClientWorld,_serverWorld,c.Id,c.Intent));
    }
    private static void Evidence(ClientAction c) => CompanionBridge.Controls.UpdateAtomicEvidence(c.Id.ActionId,
        new { authority="pending_server", serverConfirmationStatus="pending", mutationSent=c.Sent,
            serverWorldId=c.Id.ServerWorld, nonce=c.Id.Nonce, session=c.Id.Session, generation=c.Id.Generation, actionId=c.Id.ActionId });
    private static void Finish(ClientAction c,string status,string reason,object evidence=null)
    {
        CompanionBridge.Controls.FinishAtomic(c.Id.ActionId,status,reason,evidence,
            new(true,new(Main.LocalPlayer.position.X,Main.LocalPlayer.position.Y),new(Main.LocalPlayer.velocity.X,Main.LocalPlayer.velocity.Y),Main.LocalPlayer.statLife,Main.LocalPlayer.statLifeMax2));
    }
    // Called after the existing target/selection/reach checks, before any local
    // completion predicate or ItemCheck input. true means this frame must wait.
    internal static bool Before(Player player,MovementSnapshot action,ControlRequest request)
    {
        if(Main.netMode!=NetmodeID.MultiplayerClient || !Required(request.Operation)) return false;
        if(!Current(_client))
        {
            if(player.chest < -1)
            {
                CompanionBridge.Controls.FinishAtomic(action.ActionId,"failed","unsupported_bank_state_during_authority_action",null);
                return true;
            }
            var a=request.Arguments; int x=a.GetProperty("x").GetInt32(), y=a.GetProperty("y").GetInt32();
            var intent=new AuthorityIntent(request.Operation,x,y,player.selectedItem,player.HeldItem.type,player.HeldItem.stack,player.chest,World.Capture(request.Operation,x,y));
            var id=new AuthorityIdentity(Guid.ParseExact(action.WorldSessionId,"N"),_serverWorld,action.Session,action.Generation,action.ActionId,Guid.NewGuid());
            _client=new() { Id=id, Intent=intent, Started=Now, Renewed=Now };
            Evidence(_client);
            if(_serverWorld==Guid.Empty) Send(new(AuthorityKind.Hello,id.ClientWorld)); else Arm(_client);
        }
        var c=_client;
        if(c.Error!=null) { Finish(c,"unknown",c.Error); return true; }
        if(c.Outcome is {} result)
        {
            if(result.Status=="completed" && !World.ClientMatches(player,c.Intent,result))
            {
                if(Now-c.Received>1000) Finish(c,"unknown","server_result_client_state_not_converged");
                return true;
            }
            Finish(c,result.Status,result.Reason,new { authority="server_native_handler", serverConfirmationStatus="known",
                serverWorldId=c.Id.ServerWorld, nonce=c.Id.Nonce, session=c.Id.Session, generation=c.Id.Generation, actionId=c.Id.ActionId,
                serverUpdate=result.ServerUpdate, before=c.Intent.Cells, after=result.Cells,
                beforeStack=c.Intent.Stack, afterStack=result.Stack, chest=result.Chest,
                serverPlayerId=Main.myPlayer, nativeRequest=c.Native,
                clientStateVerified=result.Status=="completed", scope="correlated native request; downstream circuit effects not verified" });
            return true;
        }
        if(!c.Armed && Now-c.Started>2000) { Finish(c,"failed","server_authority_handshake_timeout"); return true; }
        if(c.Armed && Now-c.Renewed>=100)
        {
            Send(new(AuthorityKind.Renew,c.Id.ClientWorld,c.Id.ServerWorld,c.Id)); c.Renewed=Now;
        }
        if(c.Armed && !c.Sent && c.Native==null && !World.Unchanged(c.Intent))
        { Finish(c,"unknown","client_effect_without_correlated_native_request"); return true; }
        return !c.Armed || c.Sent || c.Native!=null;
    }
    internal static void After(Player player)
    {
        var c=_client;
        if(!Current(c) || c.Native==null || c.Sent || c.Error!=null) return;
        int after=player.inventory[c.Intent.Slot].IsAir ? 0 : player.inventory[c.Intent.Slot].stack;
        if(c.Intent.Operation=="place_wall" && after!=c.Intent.Stack-1) { c.Error="native_wall_consumption_not_verified"; return; }
        c.Sent=true; Evidence(c);
        Send(new(AuthorityKind.Execute,c.Id.ClientWorld,c.Id.ServerWorld,c.Id,Native:c.Native,AfterStack:after));
    }
    public override void PostUpdateEverything()
    {
        for(int i=0;i<64 && Incoming.TryDequeue(out var incoming);i++)
        {
            if(Main.netMode==NetmodeID.Server)
            {
                var response=Authority.Handle(incoming.Sender,incoming.Message,Now,World);
                if(response!=null)
                {
                    if(response.Kind==AuthorityKind.Result) World.Sync(incoming.Sender,response.Intent,response.Outcome);
                    Send(response,incoming.Sender);
                }
            }
            else if(Main.netMode==NetmodeID.MultiplayerClient)
            {
                var m=incoming.Message; var c=_client;
                if(!Current(c) || c.Id.ClientWorld!=m.ClientWorld) continue;
                if(m.Kind==AuthorityKind.Welcome && c.Id.ServerWorld==Guid.Empty)
                { _serverWorld=m.ServerWorld; Arm(c); continue; }
                if(c.Id!=m.Identity || m.ServerWorld!=_serverWorld) continue;
                if(m.Kind==AuthorityKind.Armed) c.Armed=true;
                if(m.Kind==AuthorityKind.Result) { c.Outcome=m.Outcome; c.Received=Now; }
            }
        }
        if(Main.netMode!=NetmodeID.MultiplayerClient || _client==null) return;
        var client=_client;
        if(!Current(client))
        {
            if(!client.CancelSent && client.Id.ServerWorld!=Guid.Empty)
            { client.CancelSent=true; Send(new(AuthorityKind.Cancel,client.Id.ClientWorld,client.Id.ServerWorld,client.Id)); }
            return;
        }
        After(Main.LocalPlayer); // interaction packets are captured before ItemCheck too
    }
    private static void Capture(On_NetMessage.orig_SendData orig,int msgType,int remoteClient,int ignoreClient,NetworkText text,
        int number,float number2,float number3,float number4,int number5,int number6,int number7)
    {
        var c=_client;
        if(Main.netMode==NetmodeID.MultiplayerClient && Current(c))
        {
            if(c.Intent.Operation=="place_wall" && c.Armed && msgType==5 && number==Main.myPlayer && number2==c.Intent.Slot) return;
            if(_inputScope && msgType is 17 or 19 or 20 or 31 or 33 or 59)
            {
                if(msgType==20) { c.Error="unsupported_native_tile_square_mutation"; return; }
                var native=msgType switch {
                    17 => new NativeMutation(17,number,(int)number2,(int)number3,(int)number4,number5),
                    19 => new NativeMutation(19,number,(int)number2,(int)number3,(int)number4),
                    31 or 59 => new NativeMutation(msgType,0,number,(int)number2,0),
                    _ => new NativeMutation(33,number,0,0,0) };
                // Failed hammer hits are visual/progress messages, not mutations.
                if(msgType==17 && native.Action==2 && native.Data==1 && native.X==c.Intent.X && native.Y==c.Intent.Y) return;
                if(!World.Allowed(c.Intent,native)) { c.Error="unexpected_native_operation_or_target"; return; }
                try { AuthorityCodec.Validate(new(AuthorityKind.Execute,c.Id.ClientWorld,c.Id.ServerWorld,c.Id,Native:native)); }
                catch(FormatException) { c.Error="invalid_native_packet_values"; return; }
                if(c.Native!=null) { c.Error="multiple_native_mutations_in_one_action"; return; }
                c.Native=native; return;
            }
        }
        orig(msgType,remoteClient,ignoreClient,text,number,number2,number3,number4,number5,number6,number7);
    }
}
