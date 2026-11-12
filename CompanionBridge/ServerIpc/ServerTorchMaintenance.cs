using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Holds the same actor's execution lane until native use and inventory cleanup
// settle. An admitted Agent action takes precedence over starting any new use.
internal sealed class ServerTorchMaintenance
{
    private const int Spacing=8;
    private const ulong Interval=120, ReservationUpdates=180, UseBudget=90;
    private readonly ServerAiNativeActions executor;
    private readonly Dictionary<Point,ulong> reservations=new();
    private ulong nextCheck,lastUpdate,started;
    private AiReadOnlyProtocol.Identity owner;
    private Point target;
    private int source,hotbar,selected,initialStack;
    private Item torch,empty;
    private ActorInventory.Context swap,restore;
    private object use;
    private AiAtomicActions.NativeResult result;
    private bool cleaning,restoreSelection,disabled;
    internal bool Busy=>owner!=null;

    internal ServerTorchMaintenance(ServerAiNativeActions executor)=>this.executor=executor;

    internal void Tick(AiReadOnlyProtocol.Identity id,bool agentActive)
    {
        if(id==null||!ServerAiNativeActions.Owned(id))return;
        ulong now=id.GameUpdate;
        if(lastUpdate==now)return;
        lastUpdate=now;
        var p=(Player)id.Actor;
        if(owner!=null)
        {
            if(!TaskSlot.Same(owner,id)){Clear();reservations.Clear();return;}
            Advance(id,p,agentActive);
            return;
        }
        if(disabled||agentActive||now<nextCheck||!Suitable(p))return;
        nextCheck=now+Interval;
        source=Array.FindIndex(p.inventory,0,50,i=>i.type==ItemID.Torch&&!i.IsAir&&ActorPlacement.Supported(i));
        if(source<0)return;
        hotbar=source<10?source:Array.FindIndex(p.inventory,0,10,i=>i.IsAir);
        if(hotbar<0)return;
        var lighting=ServerAiSystem.Lighting;
        if(lighting==null||!lighting.Sample(p)||lighting.Brightness>=ServerTileLighting.Threshold)return;
        foreach(var expired in reservations.Where(pair=>pair.Value<=now).Select(pair=>pair.Key).ToArray())reservations.Remove(expired);
        var site=FindSite(p,lighting);
        if(!site.HasValue)return;
        target=site.Value;reservations[target]=now+ReservationUpdates;
        selected=p.selectedItem;torch=p.inventory[source];empty=p.inventory[hotbar];initialStack=torch.stack;
        owner=id;
        if(source!=hotbar)swap=Swap(id,p,source,hotbar);
        Advance(id,p,false);
    }

    private void Advance(AiReadOnlyProtocol.Identity id,Player p,bool agentActive)
    {
        if(!cleaning&&swap!=null&&swap.Status=="running")
        {
            if(agentActive&&!swap.MutationAttempted)ActorInventory.Cancel(swap);
            else ActorInventory.Step(swap);
            if(swap.Status=="running")return;
            if(swap.Status!="completed")cleaning=true;
        }
        if(!cleaning&&use==null)
        {
            if(agentActive||!Suitable(p)||!ValidSite(p,target)||!ReferenceEquals(p.inventory[hotbar],torch))
                cleaning=true;
            else
            {
                restoreSelection=p.selectedItem!=hotbar;
                if(restoreSelection)ActorItemUse.Select(p,id.World,id.Generation,hotbar);
                started=id.GameUpdate;
                use=executor.StartMaintenancePlacement(id,target);
                return;
            }
        }
        if(!cleaning&&use!=null)
        {
            result=executor.Poll(id,use);
            if(result.Status=="running")
            {
                if(id.GameUpdate-started<UseBudget&&id.CanAct&&!Threatened(p))return;
                result=executor.Cancel(id,use);
            }
            cleaning=true;
        }
        if(use!=null&&result?.InputsReleased!=true)
        {
            result=executor.Cancel(id,use);
            if(!result.InputsReleased)throw new InvalidOperationException("Torch input release unconfirmed.");
        }
        if(restoreSelection&&id.CanAct&&p.selectedItem==hotbar)
            ActorItemUse.Select(p,id.World,id.Generation,selected);
        restoreSelection=false;
        // Restore only slots that still match the swap and the native use.
        // A pickup or unconfirmed mutation must never be overwritten.
        if(source!=hotbar&&restore==null&&id.CanAct&&ReferenceEquals(p.inventory[source],empty)&&empty.IsAir
            &&ReferenceEquals(p.inventory[hotbar],torch)&&(torch.IsAir||torch.type==ItemID.Torch&&torch.stack<=initialStack))
            restore=Swap(id,p,hotbar,source);
        if(restore!=null&&restore.Status=="running")
        {
            ActorInventory.Step(restore,confirmOnlyMovedSlots:true);
            if(restore.Status=="running")return;
        }
        if(result!=null||swap?.MutationAttempted==true)
        {
            var evidence=new{x=target.X,y=target.Y,id.GameUpdate,source,hotbar,initialStack,
                selectedBefore=selected,selectedAfter=p.selectedItem,restoreStatus=restore?.Status,restoreReason=restore?.Reason,
                remaining=p.inventory[source].type==ItemID.Torch?p.inventory[source].stack:0,result};
            ProbeSession.Log("auto-torch-result",evidence);
            ServerTorchFixture.Record(evidence);
        }
        Clear();
    }

    internal void Recover()
    {
        disabled=true;
        if(!ServerAiNativeActions.Owned(owner)){Clear();return;}
        if(use!=null&&result?.Status is (null or "running"))result=executor.Cancel(owner,use);
        cleaning=true;
        // Follow the same guarded cleanup path on the next simulation update.
    }

    internal void Stop()
    {
        if(!ServerAiNativeActions.Owned(owner)){Clear();return;}
        Recover();
        var p=(Player)owner.Actor;
        Advance(owner with{CanAct=!p.dead&&!p.ghost},p,true);
        Clear();
    }

    private void Clear()
    {owner=null;swap=restore=null;use=null;result=null;cleaning=restoreSelection=false;torch=empty=null;}

    private static ActorInventory.Context Swap(AiReadOnlyProtocol.Identity id,Player p,int from,int to)
        =>ActorInventory.Begin(id,"swap_inventory_slots",JsonSerializer.SerializeToElement(new{
            sourceSlot=from,destinationSlot=to,sourceToken=ActorInventory.Token(id,p.inventory[from],from),
            destinationToken=ActorInventory.Token(id,p.inventory[to],to)}));

    private static Vector2 Center(Point point)=>new(point.X*16+8,point.Y*16+8);
    private static bool Visible(Vector2 a,Vector2 b)=>Collision.CanHitLine(a,1,1,b,1,1);
    private static bool Suitable(Player p)=>p.active&&!p.dead&&!p.ghost&&!p.noItems&&!p.noBuilding&&!p.CCed&&!p.cursed&&!p.frozen
        &&!p.stoned&&!p.webbed&&!p.pulley&&!p.mount.Active&&!p.wet&&!p.shimmerWet&&p.grapCount==0&&p.gravDir==1
        &&p.selectedItem is >=0 and <10&&p.itemAnimation==0&&p.itemTime==0&&p.reuseDelay==0&&p.velocity.LengthSquared()<.04f
        &&ServerAiNativeActions.Released(p)&&!ActorItemUse.Running&&!ActorControl.Running&&!ActorMining.Running&&!Threatened(p)
        &&NativeMotionWorld.Observe(p).Grounded;

    private static bool Threatened(Player p)=>p.immune
        ||Main.npc.Any(n=>n.active&&!n.friendly&&n.damage>0&&Vector2.DistanceSquared(n.Center,p.Center)<320*320&&Visible(n.Center,p.Center))
        ||Main.projectile.Any(q=>q.active&&q.hostile&&q.damage>0&&Vector2.DistanceSquared(q.Center,p.Center)<240*240);

    private static bool ValidSite(Player p,Point at)
    {
        int x=at.X,y=at.Y;
        if(!WorldGen.InWorld(x,y,10)||!TerrainReader.Loaded(x,y))return false;
        var tile=Main.tile[x,y];
        var below=Main.tile[x,y+1];
        return !tile.HasTile&&tile.LiquidAmount==0
            &&(tile.WallType>0||WorldGen.SolidTile(x,y+1)||WorldGen.SolidTile(x-1,y)||WorldGen.SolidTile(x+1,y)
                ||below.HasUnactuatedTile&&TileID.Sets.Platforms[below.TileType])
            &&p.IsInTileInteractionRange(x,y,TileReachCheckSettings.Simple)&&Visible(p.Center,Center(at));
    }

    private Point? FindSite(Player p,ServerTileLighting lighting)
    {
        var center=p.Center.ToTileCoordinates();
        foreach(int dx in new[]{0,-1,1,-2,2,-3,3})
        for(int dy=1;dy>=-2;dy--)
        {
            var at=new Point(center.X+dx,center.Y+dy);
            if(!ValidSite(p,at))continue;
            var world=Center(at);
            bool Near(Point other,int radius)=>Vector2.DistanceSquared(world,Center(other))<radius*radius*256&&Visible(world,Center(other));
            if(reservations.Keys.Any(other=>Near(other,Spacing)))continue;
            bool crowded=false;
            for(int x=at.X-Spacing;x<=at.X+Spacing&&!crowded;x++)
                for(int y=at.Y-Spacing;y<=at.Y+Spacing&&!crowded;y++)
                {
                    var tile=Main.tile[x,y];
                    if(tile.HasUnactuatedTile&&tile.TileType==TileID.Torches)
                        crowded=Near(new(x,y),lighting.Source(x,y)>=ServerTileLighting.Threshold?Spacing:2);
                }
            if(!crowded)return at;
        }
        return null;
    }
}
