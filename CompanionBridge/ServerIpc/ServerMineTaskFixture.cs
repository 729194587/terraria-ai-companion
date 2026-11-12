using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal static class ServerMineTaskFixture
{
    private static bool expireMining, obscureMining,changeNavigationCut;
    internal static bool Enabled=>ServerAiActionFixture.Enabled&&Program.LaunchParameters.ContainsKey("-companiontasktest");
    internal static void Tick(Player p)
    {
        if(!Enabled)return;
        if(changeNavigationCut&&ActorMining.Running&&ActorMining.Current.Strikes>0)
        {
            var cut=ActorMining.Current;Main.tile[cut.X,cut.Y].TileType=TileID.Obsidian;
            changeNavigationCut=false;
        }
        if(expireMining&&ActorMining.Running&&ActorMining.Current.Strikes>0)
        {
            ActorMining.Current.Ticks=600;
            expireMining=false;
        }
        if(obscureMining&&ActorMining.Running&&ActorMining.Current.Strikes>0)
        {
            ActorMining.Stop(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration);
            ActorMining.Current.Phase=ActionPhase.Stale; // Isolated lost-effect observation.
            obscureMining=false;
        }
        string request=Path.Combine(Main.SavePath,"mine-task-request.json");if(!File.Exists(request))return;
        string requestBody;
        try
        {
            requestBody=File.ReadAllText(request);
            File.Delete(request);
        }
        catch(IOException)
        {
            // Windows can still hold the file briefly after the atomic rename.
            return;
        }
        using var document=JsonDocument.Parse(requestBody);
        string kind=document.RootElement.GetProperty("caseId").GetString();
        if(ActorControl.Running||ActorMining.Running||ActorItemUse.Running)throw new InvalidOperationException("Cannot reseed running native action");
        int left=Main.spawnTileX,floor=Main.spawnTileY+8,x=left+10,y=floor-2;
        int? destinationX=null,destinationY=null;
        object stepEvidence=null;
        if(kind is "change" or "replace_same")
        {
            if(kind=="change")Main.tile[x,y].TileType=TileID.Stone;
            else
            {
                short fx=Main.tile[x,y].TileFrameX,fy=Main.tile[x,y].TileFrameY;
                WorldGen.KillTile(x,y,false,false,true);
                if(!WorldGen.PlaceTile(x,y,TileID.Copper,false,true))throw new InvalidOperationException("Native same-type fixture replacement failed");
                Main.tile[x,y].TileFrameX=fx;Main.tile[x,y].TileFrameY=fy;
            }
        }
        else
        {
            int fixtureRight=kind=="navigation_surface_wide"?72:24;
            for(int tx=left-24;tx<=left+fixtureRight;tx++)for(int ty=floor-20;ty<=floor+16;ty++)
            {var t=Main.tile[tx,ty];t.ClearEverything();if(ty>=floor){t.HasTile=true;t.TileType=TileID.Dirt;}}
            foreach(var drop in Main.item)drop.active=false;
            Array.Clear(p.buffType);Array.Clear(p.buffTime); // Isolate from preceding native apple regression.
            p.jump=0;p.releaseJump=true;
            p.position=new(left*16,floor*16-p.height);p.velocity=Microsoft.Xna.Framework.Vector2.Zero;
            for(int i=0;i<p.inventory.Length;i++)p.inventory[i]=new Item();
            p.inventory[0]=new Item(ItemID.CopperShortsword);p.inventory[1]=new Item(ItemID.CopperPickaxe);p.selectedItem=0;
            if(kind is "near" or "hotbar" or "bag" or "underpowered" or "workbench" or "workbench_task" or "cutout" or "deadline")x=left+2;
            expireMining=kind is "deadline" or "opening_deadline";
            obscureMining=kind is "opening_unknown" or "navigation_joint_unknown";
            changeNavigationCut=kind=="navigation_joint_changed";
            if(kind is "bag" or "cutout" or "deadline")
                Utils.Swap(ref p.inventory[1],ref p.inventory[20]);
            if(kind=="cutout")
            {
                // A two-tile opening in the surface after removing the two cover tiles.
                for(int tx=left;tx<left+2;tx++)
                {
                    Main.tile[tx,floor].ClearTile();
                    Main.tile[tx,floor+1].ClearTile();
                }
                p.position=new(left*16+6,(floor+2)*16-p.height);
                y=floor+1;
            }
            if(kind is "workbench" or "workbench_task")y=floor-1;
            if(kind=="reach_corner")
            {
                // In copper pick's native rectangle, over 80px from the actor centre.
                x=left-4;
                y=floor-5;
            }
            if(kind=="opening_across")
            {
                x=left+7;y=floor-2;
                for(int tx=left+2;tx<=left+17;tx++)
                    for(int ty=floor-9;ty<floor;ty++)
                    {var rock=Main.tile[tx,ty];rock.HasTile=true;rock.TileType=TileID.Stone;}
            }
            if(kind is "opening_down" or "opening_multidrop" or "opening_probe" or "opening_hazard" or "opening_hard" or "opening_unsafe" or "opening_deadline" or "opening_unknown" or "opening_cancel")
            {
                x=left;y=floor+4;
                if(kind=="opening_probe")
                {
                    // ore8: actor left at column +3.4px; copper one column left,
                    // three rows below feet. Rebuild that layout in copied rock.
                    p.position.X+=3.402f;x=left-1;y=floor+3;
                }
                for(int tx=left-4;tx<=left+5;tx++)
                    for(int ty=floor;ty<=floor+8;ty++)Main.tile[tx,ty].TileType=TileID.Stone;
                if(kind=="opening_hard")
                    for(int tx=left;tx<=left+1;tx++)Main.tile[tx,floor].TileType=TileID.Obsidian;
                if(kind=="opening_unsafe")
                {
                    y=floor+5;
                    for(int tx=left;tx<=left+1;tx++)
                        for(int ty=floor+1;ty<=floor+4;ty++)Main.tile[tx,ty].ClearTile();
                }
                if(kind=="opening_multidrop")
                {
                    // Replay the two pass-through rows below the original grass cover.
                    for(int tx=left;tx<=left+1;tx++)for(int ty=floor+1;ty<=floor+2;ty++)
                    {var vine=Main.tile[tx,ty];vine.HasTile=true;vine.TileType=TileID.Vines;}
                    Main.tile[left+1,floor].TileType=TileID.PumpkinBlock;
                    var grass=Main.tile[left+2,floor];grass.TileType=TileID.Grass;
                    var plant=Main.tile[left+2,floor-1];plant.HasTile=true;plant.TileType=TileID.Plants2;
                    var view=new LocalTerrainView(Common.Systems.TerrainReader.ReadEnvironment);
                    if(!NativeNavigationEdits.SafeExcavation(left+1,floor,view))
                        throw new InvalidOperationException("Plants2 rejected an adjacent PumpkinBlock cut");
                    var above=Main.tile[left+1,floor-1];above.HasTile=true;above.TileType=TileID.Sand;
                    if(NativeNavigationEdits.SafeExcavation(left+1,floor,view))
                        throw new InvalidOperationException("Opening allowed removal of a sand column's support");
                    above.ClearTile();above.LiquidType=LiquidID.Lava;above.LiquidAmount=255;
                    if(NativeNavigationEdits.SafeExcavation(left+1,floor,view))
                        throw new InvalidOperationException("Opening allowed removal beside lava");
                    above.LiquidAmount=0;
                    for(int col=0;col<2;col++)
                    {
                        var part=Main.tile[left+8+col,floor-1];part.HasTile=true;part.TileType=TileID.WorkBenches;
                        part.TileFrameX=(short)(col*18);part.TileFrameY=0;
                    }
                    if(NativeNavigationEdits.SafeExcavation(left+8,floor,view)
                        ||!NativeNavigationEdits.SafeExcavation(left+7,floor,view))
                        throw new InvalidOperationException("Opening must protect the workbench's anchor, not nearby unrelated blocks");
                    for(int col=0;col<2;col++)Main.tile[left+8+col,floor-1].ClearTile();
                }
                if(kind=="opening_hazard")
                {
                    var lava=Main.tile[left,floor+1];lava.ClearTile();lava.LiquidType=LiquidID.Lava;lava.LiquidAmount=255;
                }
            }
            var ore=Main.tile[x,y];ore.HasTile=true;ore.TileType=kind=="underpowered"?TileID.Obsidian:TileID.Copper;
            if(kind is "workbench" or "workbench_task")
            {
                for(int col=0;col<2;col++)
                {
                    var part=Main.tile[x+col,y];part.HasTile=true;part.TileType=TileID.WorkBenches;
                    part.TileFrameX=(short)(col*18);part.TileFrameY=0;
                }
                x++; // Mine the non-origin segment.
            }
            if(kind=="blocked")
                for(int tx=left+4;tx<=left+4;tx++)for(int ty=floor-12;ty<floor;ty++){var t=Main.tile[tx,ty];t.HasTile=true;t.TileType=TileID.Obsidian;}
            if(kind=="no_tool")p.inventory[1]=new Item();
            if(kind.StartsWith("navigation_",StringComparison.Ordinal))
            {
                ore.ClearTile();
                destinationX=(left+5)*16+8;
                destinationY=floor*16;
                if(kind is "navigation_single" or "navigation_single_platform" or "navigation_half" or "navigation_slope")
                {
                    for(int tx=left-2;tx<=left+5;tx++)Main.tile[tx,floor].ClearTile();
                    var support=Main.tile[left+1,floor];
                    support.HasTile=true;
                    support.TileType=kind=="navigation_single_platform"?TileID.Platforms:TileID.Stone;
                    support.IsHalfBlock=kind=="navigation_half";
                    if(kind=="navigation_slope")support.Slope=SlopeType.SlopeDownRight;
                    int feetOffset=kind=="navigation_half"?8:kind=="navigation_slope"?6:0;
                    int playerX=kind=="navigation_slope"?(left+1)*16+6:(left+2)*16-2;
                    destinationX=playerX+10;
                    destinationY=floor*16+feetOffset;
                    p.position=new(playerX,destinationY.Value-p.height-4);
                }
                if(kind=="navigation_trip")
                {
                    destinationX=(left+20)*16+8;
                    foreach(int column in new[]{3,11})
                        for(int ty=floor-2;ty<floor;ty++)
                        {var rock=Main.tile[left+column,ty];rock.HasTile=true;rock.TileType=TileID.Stone;}
                }
                if(kind=="navigation_surface_wide")
                    destinationX=(left+60)*16+8;
                if(kind=="navigation_cost_detour")
                    for(int tx=left+2;tx<=left+3;tx++)for(int ty=floor-2;ty<floor;ty++)
                    {var rock=Main.tile[tx,ty];rock.HasTile=true;rock.TileType=TileID.Stone;}
                if(kind is "navigation_joint_bridge" or "navigation_joint_no_material" or "navigation_joint_changed" or "navigation_joint_unknown")
                {
                    for(int tx=left+2;tx<=left+3;tx++)for(int ty=floor;ty<=floor+8;ty++)Main.tile[tx,ty].ClearTile();
                    for(int ty=floor-10;ty<floor;ty++)
                    {var rock=Main.tile[left+2,ty];rock.HasTile=true;rock.TileType=TileID.Stone;}
                    if(kind!="navigation_joint_no_material")p.inventory[2]=new Item(ItemID.WoodPlatform){stack=4};
                }
                if(kind is "navigation_step_half_roof" or "navigation_step_up_roof" or "navigation_step_down_roof")
                {
                    bool half=kind=="navigation_step_half_roof",down=kind=="navigation_step_down_roof";
                    for(int tx=left-4;tx<=left+14;tx++)
                    {
                        var roof=Main.tile[tx,floor-5];roof.HasTile=true;roof.TileType=TileID.Stone;
                        if(down?tx<left+3:tx>=left+3)
                        {var step=Main.tile[tx,floor-1];step.HasTile=true;step.TileType=TileID.Stone;step.IsHalfBlock=half;}
                    }
                    if(down)p.position.Y-=16;
                    destinationY=floor*16-(down?0:half?8:16);
                    var start=NativeMotionWorld.Observe(p);var physics=NativeNavigation.Parameters(p);
                    var goal=LandingRegion.AtDestination(destinationX.Value,destinationY.Value);
                    var graph=new SurfaceGraph();var source=new NativeMotionWorld(start);
                    foreach(int _ in graph.Refresh(source,left,floor,8,physics)){}
                    var from=new SurfaceNode(-1,left,floor,new(start.Px,start.Px,start.Py+42),start.Px,start.Px+20,SurfaceKind.Solid,0,true,true);
                    var to=new SurfaceNode(-2,left+5,floor,goal,(left+5)*16,(left+6)*16,half?SurfaceKind.Half:SurfaceKind.Solid,0,true,true);
                    var edge=graph.Candidate(from,to,physics);
                    var walk=PhysicsSimulator.Predict(start,1,0,physics,source,walkFrames:40);
                    var probe=down?start:to.Rest((goal.MinPlayerLeftX+goal.MaxPlayerLeftX)/2);
                    var jumpWorld=new NativeMotionWorld(probe);var jumped=probe;float rise=0;
                    for(int frame=0;frame<12;frame++)
                    {jumped=PhysicsSimulator.Step(jumped,new(0,true),physics,jumpWorld);rise=Math.Max(rise,probe.Py-jumped.Py);}
                    stepEvidence=new{candidate=edge?.Motion.ToString(),walkSafe=walk.Safe,walkArrived=goal.Arrived(walk.Landing,source),
                        walkJump=walk.Jump,walkReason=walk.Reason,jumpRise=rise,feetChange=walk.Landing.Py-start.Py};
                    p.inventory[1]=new Item();
                }
                if(kind=="navigation_surface_detour")
                {
                    destinationX=(left+14)*16+8;
                    for(int tx=left-1;tx<=left+5;tx++)
                    {var roof=Main.tile[tx,floor-4];roof.HasTile=true;roof.TileType=TileID.Stone;}
                    for(int ty=floor-4;ty<floor;ty++)
                    {var wall=Main.tile[left+5,ty];wall.HasTile=true;wall.TileType=TileID.Stone;}
                }
                if(kind=="navigation_long_dig")
                {
                    destinationX=(left+11)*16+8;
                    for(int tx=left+2;tx<=left+10;tx++)
                        for(int ty=floor-10;ty<floor;ty++)
                        {var rock=Main.tile[tx,ty];rock.HasTile=true;rock.TileType=TileID.Stone;}
                }
                if(kind=="navigation_long_place")
                {
                    for(int tx=left+1;tx<=left+20;tx++)
                        for(int ty=floor;ty<=floor+8;ty++)Main.tile[tx,ty].ClearTile();
                    destinationX=(left+11)*16+8;
                    p.inventory[2]=new Item(ItemID.WoodPlatform){stack=40};
                }
                if(kind is "navigation_dig" or "navigation_jump")
                    for(int tx=left+2;tx<=left+3;tx++)
                        for(int ty=floor-(kind=="navigation_dig"?10:2);ty<floor;ty++)
                        {var rock=Main.tile[tx,ty];rock.HasTile=true;rock.TileType=TileID.Stone;}
                // Isolate movement acceptance from the independently permitted
                // terrain-modification choice in these two movement fixtures.
                if(kind is "navigation_jump" or "navigation_trip" or "navigation_surface_wide" or "navigation_surface_detour")p.inventory[1]=new Item();
                if(kind=="navigation_down")
                {
                    destinationX=left*16+10;
                    destinationY=(floor+1)*16;
                }
                if(kind=="navigation_drop")
                {
                    for(int tx=left+3;tx<=left+22;tx++)Main.tile[tx,floor].ClearTile();
                    destinationY=(floor+1)*16;
                }
                if(kind.StartsWith("navigation_safety_",StringComparison.Ordinal))
                {
                    int depth=kind=="navigation_safety_drop13"?13:kind=="navigation_safety_immune"?36:
                        kind is "navigation_safety_drop27" or "navigation_safety_extra"?27:0;
                    bool dense=kind=="navigation_safety_dense",longTrip=kind=="navigation_safety_long";
                    int right=longTrip?552:32,top=dense?32:10,bottom=dense?32:depth+4;
                    for(int dx=-32;dx<=right;dx++)
                    for(int dy=-top;dy<=bottom;dy++)
                    {
                        var tile=Main.tile[left+dx,floor+dy];tile.ClearEverything();
                        int surface=dx<=2?0:depth;
                        if(dense&&dy!=0)
                        {
                            if(Math.Abs(dy)%4!=0)continue;
                            tile.HasTile=true;tile.TileType=TileID.Stone;tile.Slope=SlopeType.SlopeDownRight;
                        }
                        else if(dy>=surface){tile.HasTile=true;tile.TileType=TileID.Stone;}
                    }
                    for(int i=0;i<p.armor.Length;i++)p.armor[i]=new Item();
                    if(kind=="navigation_safety_extra")p.armor[2]=new Item(ItemID.MoonLordLegs);
                    if(kind=="navigation_safety_immune")p.armor[3]=new Item(ItemID.LuckyHorseshoe);
                    p.inventory[1]=new Item();
                    p.statLife=p.statLifeMax2;
                    p.fallStart=FallSafety.Row(p.position.Y);
                    destinationX=(left+(longTrip?530:dense?18:7))*16+8;
                    destinationY=(floor+depth)*16;
                    stepEvidence=new{depth,startX=p.position.X,startFeetY=floor*16,startLife=p.statLife};
                }
                if(kind=="navigation_place")
                {
                    for(int tx=left+1;tx<=left+20;tx++)
                        for(int ty=floor;ty<=floor+8;ty++)Main.tile[tx,ty].ClearTile();
                    destinationX=(left+2)*16+8;
                    p.inventory[2]=new Item(ItemID.WoodPlatform){stack=5};
                }
            }
        }
        NetworkStateSync.Full(p);
        File.WriteAllText(Path.Combine(Main.SavePath,"mine-task-ready.json"),JsonSerializer.Serialize(new{caseId=kind,x,y,destinationX,destinationY,stepEvidence,typeId=(int)Main.tile[x,y].TileType,world=ProbeSession.WorldSession,generation=ProbeSession.ActorGeneration,slot=p.whoAmI,gameUpdate=Main.GameUpdateCount}));
    }
}
