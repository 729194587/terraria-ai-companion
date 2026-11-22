using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.Localization;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal static class ServerPylons
{
    private sealed record Destination(TeleportPylonInfo Info, TileEntity Entity, string Token);
    private static Dictionary<TeleportPylonInfo, Destination> listed = new();
    private static Identity owner;
    private sealed class Receipt(Player actor, TeleportPylonInfo info)
    {
        internal readonly Player Actor = actor;
        internal readonly TeleportPylonInfo Info = info;
        internal Vector2? Target;
        internal string Message;
    }
    [ThreadStatic] private static Receipt pending;

    internal static void Install(Action<Action, Action> install)
    {
        install(() => On_Player.Teleport += ObserveTeleport, () => On_Player.Teleport -= ObserveTeleport);
        install(() => IL_TeleportPylonsSystem.HandleTeleportRequest += ObserveRejection,
            () => IL_TeleportPylonsSystem.HandleTeleportRequest -= ObserveRejection);
    }

    // Observe the server handler's final rejection after tML's validation hooks.
    // Other chat messages sent by mods during the request are unrelated.
    private static void ObserveRejection(ILContext il)
    {
        var cursor=new ILCursor(il);
        cursor.GotoNext(MoveType.After,i=>i.MatchCall<NetworkText>("FromKey"));
        cursor.Emit(OpCodes.Ldarg_1);
        cursor.Emit(OpCodes.Ldarg_2);
        cursor.EmitDelegate<Func<NetworkText,TeleportPylonInfo,int,NetworkText>>((text,info,slot)=>
        {
            if (pending is {} r && slot==r.Actor.whoAmI && info.Equals(r.Info)) r.Message=text.ToString();
            return text;
        });
    }

    private static void ObserveTeleport(On_Player.orig_Teleport orig, Player self, Vector2 position, int style, int extra)
    {
        orig(self, position, style, extra);
        if (pending is {} r && ReferenceEquals(self,r.Actor) && style==9 && extra==(int)r.Info.TypeOfPylon)
            r.Target=position;
    }

    internal static void Clear()
    {
        listed.Clear();
        owner=null;
    }

    private static bool SameOwner(Identity frame) => owner!=null && ReferenceEquals(owner.Actor,frame.Actor)
        && owner.World==frame.World && owner.Generation==frame.Generation && owner.Slot==frame.Slot;

    private static TileEntity EntityAt(TeleportPylonInfo info) =>
        TileEntity.ByPosition.TryGetValue(info.PositionInTiles,out var entity) ? entity : null;

    internal static object List(Identity frame)
    {
        if (!SameOwner(frame)) Clear();
        owner=frame;
        var current=new Dictionary<TeleportPylonInfo,Destination>();
        foreach (var info in Main.PylonSystem.Pylons)
        {
            var entity=EntityAt(info);
            current[info]=listed.TryGetValue(info,out var old) && ReferenceEquals(old.Entity,entity)
                ? old : new(info,entity,Guid.NewGuid().ToString("N"));
        }
        listed=current;
        return new { pylons=listed.Values.Select(d=>new { pylonRef=d.Token, type=(int)d.Info.TypeOfPylon,
            name=d.Info.ModPylon?.FullName ?? d.Info.TypeOfPylon.ToString(), x=d.Info.PositionInTiles.X,y=d.Info.PositionInTiles.Y }).ToArray() };
    }

    internal static Result Travel(Identity frame,string token)
    {
        ServerAiNativeActions.Require(frame);
        var dest=SameOwner(frame) ? listed.Values.FirstOrDefault(d=>d.Token==token) : null;
        if (dest==null || !Main.PylonSystem.Pylons.Contains(dest.Info) || !ReferenceEquals(dest.Entity,EntityAt(dest.Info)))
            return new("failed","stale_pylon_ref",new { operation="teleport_to_pylon",mutationAttempted=false,effectStatus="not_started" },true,InputsAcquired:false);

        var p=(Player)frame.Actor;
        var receipt=new Receipt(p,dest.Info);
        var noArrival=new { pylonRef=token,nativeTeleport=false };
        pending=receipt;
        try
        {
            Main.PylonSystem.HandleTeleportRequest(dest.Info,frame.Slot);
            ServerAiNativeActions.Require(frame);
            if (receipt.Target is Vector2 target)
            {
                bool arrived=p.position==target;
                return new(arrived?"completed":"unknown",arrived?"pylon_arrival_verified":"pylon_arrival_unconfirmed",
                    new { pylonRef=token,nativeTeleport=true,targetX=target.X,targetY=target.Y,actorX=p.position.X,actorY=p.position.Y },
                    true,arrived,false);
            }
            if (receipt.Message!=null)
                return new("failed","pylon_rejected",noArrival,true,InputsAcquired:false,ReasonDetail:receipt.Message);
            return new("unknown","pylon_outcome_unconfirmed",noArrival,true,InputsAcquired:false);
        }
        catch(Exception e)
        {
            return new("unknown","pylon_outcome_unconfirmed",null,true,InputsAcquired:false,ReasonDetail:e.Message);
        }
        finally { pending=null; }
    }
}
