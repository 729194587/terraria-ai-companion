using System;
using System.Collections.Generic;
using System.Text.Json;
using CompanionBridge.Common.Systems;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

// Runs on the bound Player's simulation thread. World changes and controls go
// through the same observed native children as player-requested operations.
internal sealed class BreathRescue
{
    private const int Radius=16, SearchBudget=400, ReserveTicks=120;
    private static readonly Point Up=new(0,-1);
    private static readonly Point[] Directions={new(-1,0),new(1,0),Up,new(0,1)};
    private sealed record Route(Point First,int Steps,Point? Roof=null);
    private readonly Player player;
    private readonly AiAtomicActions.IExecutor executor;
    private readonly string id;
    private readonly int startingBreath;
    private readonly ulong started;
    private object child;
    private string operation;
    private int ropesPlaced, blocksMined, jumpAttempts;
    private int lastBreath;
    private bool climbed;
    private ulong lastTick=ulong.MaxValue;
    private static readonly Result Running=new("running","seeking_air",null,false);

    internal BreathRescue(Identity frame,AiAtomicActions.IExecutor executor,string id)
    {
        player=(Player)frame.Actor;
        this.executor=executor;this.id=id;
        lastBreath=startingBreath=player.breath;started=frame.GameUpdate;
        ServerGameMessages.Breath("水下氧气不足，正在找地方换气。");
    }

    private static bool HeadWet(Player p,Vector2 pos)=>Collision.DrownCollision(pos,p.width,p.height,p.gravDir,false);
    private static bool WaterBreathing(Player p)=>p.gills || p.accMerman;
    private static bool Supported(Player p)=>p.active&&!p.dead&&!p.ghost&&!p.lavaWet&&!p.honeyWet&&!p.shimmerWet
        &&!p.shimmering&&p.gravDir==1&&!p.mount.Active&&!p.CCed;

    internal static bool NeedsAir(Identity frame,bool beforeMovement=false)
    {
        var p=(Player)frame.Actor;
        // A sudden flood can invalidate the next navigation frame before Player.Update
        // refreshes p.wet. The urgent pre-movement check reads the actual head collision.
        if(!beforeMovement && (frame.GameUpdate%12!=0||!p.wet) || !Supported(p)
            || WaterBreathing(p)||p.breath>=p.breathMax||!HeadWet(p,p.position))return false;
        if(ActorControl.Driver is ServerAiNavigation nav && nav.HasAirForPoolCrossing(p))return false;
        // A reed or another native effect can recover air while the head is wet.
        if(p.breathCD==0 && p.breath>0 && p.HeldItem.type==ItemID.BreathingReed)return false;
        int remaining=Math.Max(0,p.breath)*p.breathCDMax-p.breathCD;
        var route=FindRoute(p);
        // Building the escape column costs a placement cycle as well as travel
        // on every tile. A bare jump and an already attached rope need less air.
        bool build=route?.First==Up && route.Steps>4 && !p.accFlipper
            && !ServerAiRope.Rope((int)p.Center.X/16,ServerAiRope.Head(p.position.Y))
            && Array.FindIndex(p.inventory,0,50,ActorRope.Supported)>=0;
        int cost=route==null?Radius*40:route.Steps*(build?44:24)+(route.Roof.HasValue?120:0);
        return remaining<=cost+ReserveTicks;
    }

    private static bool Clear(Player p,Vector2 pos)
    {
        int left=(int)Math.Floor(pos.X/16), right=(int)Math.Floor((pos.X+p.width-1)/16);
        int top=(int)Math.Floor((pos.Y-2)/16),bottom=(int)Math.Floor((pos.Y+p.height-1)/16);
        if(!WorldGen.InWorld(left,top,3)||!WorldGen.InWorld(right,bottom,3))return false;
        for(int x=left;x<=right;x++)
            for(int y=top;y<=bottom;y++)
            {
                if(!TerrainReader.Loaded(x,y))return false;
                var tile=Main.tile[x,y];
                if(tile.LiquidAmount>0&&tile.LiquidType!=LiquidID.Water)return false;
            }
        return !Collision.SolidCollision(pos,p.width,p.height);
    }

    private static Route FindRoute(Player p)
    {
        bool canClimb=p.accFlipper||Array.FindIndex(p.inventory,0,50,ActorRope.Supported)>=0;
        bool CanRise(Vector2 from)
        {
            if(canClimb)return true;
            int x=(int)(from.X+p.width/2)/16,head=ServerAiRope.Head(from.Y);
            if(ServerAiRope.Rope(x,head)||ServerAiRope.Rope(x,head+1))return true;
            // Unassisted players jump from support. A deep water column alone
            // is not a ladder; search sideways for a bank or an existing rope.
            for(int drop=0;drop<4;drop++)
                if(Collision.SolidCollision(from+new Vector2(0,p.height+drop*16),p.width,16,true))return true;
            return false;
        }
        bool openColumn=true;
        for(int distance=1;distance<=Radius;distance++)
        {
            var pos=p.position+new Vector2(0,-16*distance);
            if(!Clear(p,pos)||!CanRise(pos+new Vector2(0,16))){openColumn=false;break;}
            if(!HeadWet(p,pos))return new(Up,distance);
        }
        if(openColumn)return new(Up,Radius);
        var queue=new Queue<(Point Offset,Point First,int Distance)>();
        var seen=new HashSet<Point>{Point.Zero};
        queue.Enqueue((Point.Zero,Point.Zero,0));
        int budget=SearchBudget;
        while(queue.Count>0 && budget-->0)
        {
            var node=queue.Dequeue();
            foreach(var step in Directions)
            {
                Point offset=node.Offset+step;
                if(Math.Abs(offset.X)>Radius||Math.Abs(offset.Y)>Radius||seen.Contains(offset))continue;
                Vector2 pos=p.position+offset.ToVector2()*16;
                if(!Clear(p,pos)||step==Up&&!CanRise(pos+new Vector2(0,16)))continue;
                seen.Add(offset);
                var first=node.Distance==0?step:node.First;
                if(!HeadWet(p,pos))return new(first,node.Distance+1);
                queue.Enqueue((offset,first,node.Distance+1));
            }
        }
        return RoofOpening(p);
    }

    // Only open a single layer of ordinary terrain when it separates the
    // player's full width from observed dry air. Re-read after each native cut.
    private static Route RoofOpening(Player p)
    {
        int left=(int)p.Left.X/16,right=(int)(p.Right.X-1)/16,head=(int)p.Top.Y/16;
        for(int y=head;y>=head-Radius;y--)
        {
            Point? cut=null;
            for(int x=left;x<=right;x++)
            {
                if(!TerrainReader.Loaded(x,y-1)||!TerrainReader.Loaded(x,y))return null;
                var tile=Main.tile[x,y];
                if(tile.LiquidAmount>0&&tile.LiquidType!=LiquidID.Water)return null;
                if(!tile.HasUnactuatedTile||ActorRope.IsRope(tile))continue;
                if(tile.TileType is not (TileID.Dirt or TileID.Stone or TileID.IceBlock or TileID.SnowBlock or TileID.Mud or TileID.Grass))return null;
                cut??=new Point(x,y);
            }
            if(cut==null)continue;
            for(int x=left;x<=right;x++)
            {
                var air=Main.tile[x,y-1];
                if(air.HasUnactuatedTile||air.LiquidAmount!=0)return null;
            }
            return new(Up,head-y+2,cut);
        }
        return null;
    }

    internal Result Tick(Identity frame)
    {
        if(frame.GameUpdate==lastTick)return Running;
        lastTick=frame.GameUpdate;
        if(player.breath>lastBreath)jumpAttempts=0;
        lastBreath=player.breath;
        bool wet=HeadWet(player,player.position);
        bool recovered=(!wet||WaterBreathing(player))&&player.breath>=player.breathMax;
        if(child!=null)
        {
            bool stopMoving=recovered && operation is "move_input" or "breath_climb";
            var result=stopMoving?executor.Cancel(frame,child):executor.Poll(frame,child);
            if(result.Status=="running")return Running;
            child=null;
            if(!result.InputsReleased||result.Status=="unknown")return Finish("unknown",result.Reason,result.InputsReleased);
            if(stopMoving && result.Status=="cancelled")return Finish("completed","breath_recovered",true);
            if(result.Status!="completed")return Finish("failed",result.Reason,true);
            if(!result.EffectVerified)return Finish("unknown",result.Reason,true);
            if(operation=="place_rope")ropesPlaced++;
            if(operation=="mine_tile")blocksMined++;
            if(operation=="breath_climb")climbed|=player.pulley;
        }
        if(!Supported(player))return Finish("failed","breath_actor_unavailable",true);
        if(recovered)
            return Finish("completed","breath_recovered",true);
        if(frame.GameUpdate-started>=1200)return Finish("failed","breath_route_timed_out",true);
        if(!wet||WaterBreathing(player)||!ServerAiNativeActions.BodyIdle(frame))return Running;
        var route=FindRoute(player);
        if(route==null)return Finish("failed","breath_no_opening",true);
        if(route.Roof is {} tile &&
            ActorMining.ChooseTool(player,frame.World,frame.Generation,tile.X,tile.Y,true,out int pick,out _)==ActorMining.Failure.None)
            return Start(frame,"mine_tile",new{x=tile.X,y=tile.Y,plannedToolSlot=pick});
        if(route.First.X!=0)
            return Move(frame,route.First.X,false,false);
        if(route.First.Y>0)return Move(frame,0,false,false);
        int col=(int)player.Center.X/16,head=ServerAiRope.Head(player.position.Y);
        if(ServerAiRope.Rope(col,head)&&ServerAiRope.Rope(col,head-1))
            return Move(frame,0,false,true);
        if(!player.pulley && (player.accFlipper || route.Steps<=4 && jumpAttempts<2))
        {
            if(TerrainReader.Grounded(player))jumpAttempts++;
            return Move(frame,0,true,false);
        }
        int source=Array.FindIndex(player.inventory,0,50,ActorRope.Supported);
        if(source>=0 && RopeSite(col,head,player.inventory[source]) is {} site)
        {
            if(source>=10 && ActorRope.FindSlot(player)<0)
            {
                int dest=Array.FindIndex(player.inventory,0,10,i=>i.IsAir);
                if(dest<0)dest=player.selectedItem is >=0 and <10?player.selectedItem:0;
                return Start(frame,"swap_inventory_slots",new{sourceSlot=source,destinationSlot=dest,
                    sourceToken=ActorInventory.Token(frame,player.inventory[source],source),
                    destinationToken=ActorInventory.Token(frame,player.inventory[dest],dest)});
            }
            return Start(frame,"place_rope",new{x=site.X,y=site.Y});
        }
        if(player.velocity.Y!=0 || jumpAttempts<2)
        {
            if(TerrainReader.Grounded(player))jumpAttempts++;
            return Move(frame,0,true,false);
        }
        return Finish("failed",route.Roof.HasValue?"breath_roof_unreachable":"breath_no_climb",true);
    }

    private Point? RopeSite(int col,int head,Item rope)
    {
        for(int y=head-2;y<=(int)player.Bottom.Y/16+1;y++)
        {
            if(!WorldGen.InWorld(col,y,3)||!ActorRope.InReach(player,rope,col,y))continue;
            var tile=Main.tile[col,y];
            if(tile.HasTile || tile.LiquidAmount>0&&tile.LiquidType!=LiquidID.Water)continue;
            if(tile.WallType>0 || ServerAiRope.Rope(col,y+1)||ServerAiRope.Rope(col,y-1)
                ||WorldGen.SolidTile(col,y+1)||WorldGen.SolidTile(col,y-1)||WorldGen.SolidTile(col-1,y)||WorldGen.SolidTile(col+1,y))
                return new(col,y);
        }
        return null;
    }

    private Result Move(Identity frame,int side,bool jump,bool rope)
    {
        // One neutral update rearms the native releaseJump latch between jumps.
        int updates=jump?21:8;
        return Start(frame,rope?"breath_climb":"move_input",new{
            horizontal=side==0?"none":side<0?"left":"right",vertical=rope?"up":"none",
            jumpHoldUpdates=jump?updates-1:0,maxUpdates=updates});
    }
    private Result Start(Identity frame,string op,object args)
    {
        operation=op;
        child=executor.Start(frame,op,JsonSerializer.SerializeToElement(args),id);
        return Running;
    }
    internal Result Stop(Identity frame)
    {
        var result=child==null?null:executor.Cancel(frame,child);
        child=null;
        bool released=result?.InputsReleased??ServerAiNativeActions.Released(player);
        return Finish(released&&result?.Status!="unknown"?"cancelled":"unknown","breath_interrupted",released);
    }
    private Result Finish(string status,string reason,bool released)
    {
        ServerGameMessages.Breath(reason switch
        {
            "breath_recovered" when status=="completed" => "已恢复呼吸。",
            "breath_no_opening" => "水下氧气不足，附近没找到能到达的透气口。",
            "breath_roof_unreachable" => "上方有空气，但角色没能挖通水面的顶。",
            "breath_no_climb" => "水面太高，角色没能爬到能换气的位置。",
            _ => "换气行动结束："+reason
        });
        return new(status,reason,new{startingBreath,breath=player.breath,player.breathMax,ropesPlaced,blocksMined,climbed,
            headSubmerged=HeadWet(player,player.position)},released,status=="completed");
    }
}
