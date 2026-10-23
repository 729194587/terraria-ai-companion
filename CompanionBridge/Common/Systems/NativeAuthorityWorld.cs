using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace CompanionBridge.Common.Systems;

internal sealed class NativeAuthorityWorld : IAtomicAuthorityWorld
{
    private static AuthorityCell Cell(int x,int y)
    {
        if(!WorldGen.InWorld(x,y,1)) return new(x,y,false,0,0,false,0,0,0,false);
        var t=Main.tile[x,y]; return new(x,y,t.HasTile,t.TileType,t.WallType,t.IsHalfBlock,(int)t.Slope,t.TileFrameX,t.TileFrameY,t.IsActuated);
    }
    internal AuthorityCell[] Capture(string operation,int x,int y)
    {
        if(!WorldGen.InWorld(x,y,3)) return new[]{Cell(x,y)};
        var t=Main.tile[x,y];
        if(operation!="interact_tile" || !t.HasTile) return new[]{Cell(x,y)};
        var a=TileObjectData.TopLeft(x,y);
        int width=t.TileType==TileID.Switches ? 1 : 2, height=t.TileType==TileID.Switches ? 1 : 2, left=a.X;
        if(t.TileType is TileID.ClosedDoor or TileID.OpenDoor) { left--; width=4; height=3; }
        var cells=new List<AuthorityCell>();
        for(int dy=0;dy<height;dy++) for(int dx=0;dx<width;dx++) cells.Add(Cell(left+dx,a.Y+dy));
        return cells.ToArray();
    }
    private static AuthorityCell Target(AuthorityIntent t) => t.Cells.Single(c=>c.X==t.X && c.Y==t.Y);
    public string Validate(int actor,AuthorityIntent t)
    {
        var p=Main.player[actor];
        if(!p.active || p.dead || p.ghost || p.noBuilding) return "server_player_not_ready_or_building_disabled";
        if(!WorldGen.InWorld(t.X,t.Y,3) || t.Cells.Any(c=>!WorldGen.InWorld(c.X,c.Y,1))) return "server_target_out_of_world";
        if(Vector2.Distance(p.Center,new(t.X*16+8,t.Y*16+8))>80) return "server_out_of_conservative_reach";
        if(!p.InInteractionRange(t.X,t.Y,Terraria.DataStructures.TileReachCheckSettings.Simple)) return "server_out_of_native_range";
        if(p.selectedItem!=t.Slot || t.Slot>=p.inventory.Length || p.inventory[t.Slot].type!=t.ItemType || p.inventory[t.Slot].stack!=t.Stack) return "server_selected_item_or_stack_changed";
        if(!Capture(t.Operation,t.X,t.Y).SequenceEqual(t.Cells)) return "server_target_changed";
        for(int x=t.X-2;x<=t.X+2;x++) for(int y=t.Y-2;y<=t.Y+2;y++)
            if(!Netplay.Clients[actor].TileSections[Netplay.GetSectionX(x),Netplay.GetSectionY(y)]) return "server_target_section_not_sent";
        var item=p.inventory[t.Slot]; var tile=Main.tile[t.X,t.Y];
        if(t.Operation=="interact_tile")
        {
            if(!tile.HasTile || tile.IsActuated || tile.TileType is not (TileID.ClosedDoor or TileID.OpenDoor or TileID.Containers or TileID.Containers2 or TileID.Switches or TileID.Lever)) return "server_unsupported_interaction";
            var a=TileObjectData.TopLeft(t.X,t.Y);
            int width=tile.TileType is TileID.Switches or TileID.ClosedDoor ? 1 : 2;
            int height=tile.TileType is TileID.ClosedDoor or TileID.OpenDoor ? 3 : tile.TileType==TileID.Switches ? 1 : 2;
            for(int dx=0;dx<width;dx++) for(int dy=0;dy<height;dy++)
                if(!Main.tile[a.X+dx,a.Y+dy].HasTile || Main.tile[a.X+dx,a.Y+dy].TileType!=tile.TileType || Main.tile[a.X+dx,a.Y+dy].IsActuated) return "server_invalid_object_footprint";
            if(tile.TileType is TileID.Containers or TileID.Containers2)
            {
                int chest=Chest.FindChest(a.X,a.Y);
                if(chest<0 || Chest.IsLocked(a.X,a.Y)) return "server_missing_or_locked_chest";
                if(Chest.UsingChest(chest) is int owner && owner>=0 && owner!=actor) return "server_chest_in_use";
                if(p.chest!=t.ChestBefore) return "server_chest_state_changed";
            }
            return null;
        }
        if(item.IsAir || item.ModItem!=null || !ItemLoader.CanUseItem(item,p)) return "server_item_not_usable";
        int oldX=Player.tileTargetX,oldY=Player.tileTargetY;
        try { Player.tileTargetX=t.X; Player.tileTargetY=t.Y; if(!p.IsTargetTileInItemRange(item)) return "server_out_of_native_item_range"; }
        finally { Player.tileTargetX=oldX; Player.tileTargetY=oldY; }
        if(t.Operation=="place_wall")
        {
            if(tile.WallType!=0 || item.createWall<=0 || item.createTile>=0 || !item.consumable || p.autoPaint) return "server_invalid_wall_placement_item_or_target";
            var offsets=new[]{(-1,0),(1,0),(0,-1),(0,1)};
            if(!offsets.Any(d=>Main.tile[t.X+d.Item1,t.Y+d.Item2].HasTile || Main.tile[t.X+d.Item1,t.Y+d.Item2].WallType!=0)) return "server_wall_requires_native_adjacency";
            if(t.Stack>1) foreach(var (dx,dy) in offsets)
            {
                int x=t.X+dx,y=t.Y+dy;
                if(Main.tile[x,y].WallType==0 && offsets.All(d=>x+d.Item1==t.X && y+d.Item2==t.Y || Main.tile[x+d.Item1,y+d.Item2].WallType==item.createWall)) return "server_unsupported_neighbour_wall_autofill";
            }
            return null;
        }
        if(item.hammer<=0 || item.pick>0 || item.axe>0 || item.createTile>=0 || item.createWall>=0) return "server_requires_pure_hammer";
        if(t.Operation=="mine_wall") return tile.WallType==0 || tile.HasTile || !Player.CanPlayerSmashWall(t.X,t.Y) ? "server_wall_not_smashable" : null;
        if(!tile.HasTile || tile.IsActuated || tile.WallType!=0 || TileLoader.GetTile(tile.TileType)!=null || Main.tileHammer[tile.TileType]
            || (!Main.tileSolid[tile.TileType] && !TileID.Sets.Platforms[tile.TileType])
            || (Main.tileFrameImportant[tile.TileType] && !TileID.Sets.Platforms[tile.TileType]) || !WorldGen.CanPoundTile(t.X,t.Y)) return "server_unsupported_hammer_target";
        return null;
    }
    internal bool Allowed(AuthorityIntent t,NativeMutation n)
    {
        var before=Target(t);
        if(t.Operation=="place_wall") return n.Message==17 && n.Action==3 && n.X==t.X && n.Y==t.Y && n.Data>0 && n.Style==0;
        if(t.Operation=="mine_wall") return n.Message==17 && n.Action==2 && n.X==t.X && n.Y==t.Y && n.Data==0 && n.Style==0;
        if(t.Operation=="hammer_tile") return n.Message==17 && n.X==t.X && n.Y==t.Y && n.Style==0 && (n.Action==7 && n.Data==1 || n.Action==14 && n.Data is >=0 and <=4);
        if(before.Type is TileID.ClosedDoor or TileID.OpenDoor) return n.Message==19 && n.X==t.X && n.Y==t.Y && n.Style==0 && n.Action==(before.Type==TileID.ClosedDoor?0:1) && n.Data is -1 or 1;
        if(before.Type is TileID.Switches or TileID.Lever) return n.Message==59 && n.X==t.X && n.Y==t.Y && n.Action==0 && n.Data==0 && n.Style==0;
        if(before.Type is TileID.Containers or TileID.Containers2)
        {
            var a=TileObjectData.TopLeft(t.X,t.Y); int index=Chest.FindChest(a.X,a.Y);
            return t.ChestBefore==index ? n.Message==33 && n.Action==-1 && n.X==0 && n.Y==0 && n.Data==0 && n.Style==0
                : n.Message==31 && n.X==a.X && n.Y==a.Y && n.Action==0 && n.Data==0 && n.Style==0;
        }
        return false;
    }
    public AuthorityOutcome Execute(int actor,AuthorityIntent t,NativeMutation n,int afterStack)
    {
        if(!Allowed(t,n)) return Rejected(actor,t,"failed","server_native_request_not_allowed");
        var p=Main.player[actor]; var item=p.inventory[t.Slot];
        if(t.Operation=="place_wall" && (n.Data!=item.createWall || afterStack!=t.Stack-1)) return Rejected(actor,t,"failed","server_wall_consumption_or_type_mismatch");
        Native(actor,n); // the installed game's normal network handler, not custom tile writes
        var after=t.Cells.Select(c=>Cell(c.X,c.Y)).ToArray(); var old=Target(t); var current=after.Single(c=>c.X==t.X && c.Y==t.Y);
        bool changed=t.Operation switch {
            "place_wall" => current.Wall==item.createWall,
            "mine_wall" => old.Wall>0 && current.Wall==0,
            "hammer_tile" => current.HasTile && current.Type==old.Type && (current.Half!=old.Half || current.Slope!=old.Slope),
            _ => InteractionChanged(t,after,p) };
        if(!changed) return Rejected(actor,t,"failed","server_native_effect_not_verified");
        if(t.Operation=="place_wall")
        {
            if(!ReferenceEquals(p.inventory[t.Slot],item) || item.type!=t.ItemType || item.stack!=t.Stack)
                return Rejected(actor,t,"unknown","server_inventory_changed_during_native_execution");
            if(!ItemLoader.ConsumeItem(item,p)) return Rejected(actor,t,"failed","server_wall_consumption_hook_refused");
            item.stack--;
            if(item.stack<=0) item.TurnToAir();
            if(!ReferenceEquals(p.inventory[t.Slot],item) || (item.IsAir?0:item.stack)!=t.Stack-1) return Rejected(actor,t,"unknown","server_wall_consumption_not_verified");
        }
        return new("completed","server_native_effect_verified",after,item.IsAir?0:item.stack,item.type,p.chest,Main.GameUpdateCount);
    }
    private static bool InteractionChanged(AuthorityIntent t,AuthorityCell[] after,Player p)
    {
        var old=Target(t);
        if(old.Type is TileID.Containers or TileID.Containers2)
        {
            var a=TileObjectData.TopLeft(t.X,t.Y); int index=Chest.FindChest(a.X,a.Y);
            return index>=0 && p.chest==(t.ChestBefore==index?-1:index);
        }
        if(old.Type is TileID.ClosedDoor or TileID.OpenDoor)
        {
            int expected=old.Type==TileID.ClosedDoor?TileID.OpenDoor:TileID.ClosedDoor;
            return after.Zip(t.Cells).Count(pair=>pair.First.HasTile && pair.First.Type==expected && pair.Second.Type!=expected)>=3;
        }
        return after.Zip(t.Cells).All(pair=>pair.First.HasTile && pair.First.Type==old.Type
            && (pair.First.FrameX!=pair.Second.FrameX || pair.First.FrameY!=pair.Second.FrameY));
    }
    public AuthorityOutcome Rejected(int actor,AuthorityIntent t,string status,string reason)
    {
        var p=Main.player[actor]; var item=p.inventory[t.Slot];
        return new(status,reason,t.Cells.Select(c=>Cell(c.X,c.Y)).ToArray(),item.IsAir?0:item.stack,item.type,p.chest,Main.GameUpdateCount);
    }
    internal void Sync(int actor,AuthorityIntent intent,AuthorityOutcome outcome)
    {
        if(outcome?.Cells is not {Length:>0}) return;
        int left=outcome.Cells.Min(c=>c.X),top=outcome.Cells.Min(c=>c.Y);
        if(!WorldGen.InWorld(left,top,3)) return;
        NetMessage.SendTileSquare(actor,left-2,top-2,outcome.Cells.Max(c=>c.X)-left+5,outcome.Cells.Max(c=>c.Y)-top+5);
        var p=Main.player[actor];
        NetMessage.SendData(5,actor,number:actor,number2:intent.Slot);
        if(intent.Operation=="interact_tile" && Target(intent).Type is TileID.Containers or TileID.Containers2)
            NetMessage.SendData(33,actor,number:p.chest);
    }
    internal bool ClientMatches(Player p,AuthorityIntent t,AuthorityOutcome outcome)
    {
        foreach(var expected in outcome.Cells)
        {
            var actual=Cell(expected.X,expected.Y);
            if(actual.HasTile!=expected.HasTile || actual.HasTile && actual.Type!=expected.Type || actual.Wall!=expected.Wall
                || actual.Half!=expected.Half || actual.Slope!=expected.Slope || actual.Actuated!=expected.Actuated) return false;
            if(t.Operation=="interact_tile" && Target(t).Type is TileID.Switches or TileID.Lever
                && (actual.FrameX!=expected.FrameX || actual.FrameY!=expected.FrameY)) return false;
        }
        if(t.Operation=="place_wall" && ((p.inventory[t.Slot].IsAir?0:p.inventory[t.Slot].stack)!=outcome.Stack || p.inventory[t.Slot].type!=outcome.ItemType)) return false;
        if(t.Operation=="interact_tile" && Target(t).Type is TileID.Containers or TileID.Containers2 && p.chest!=outcome.Chest) return false;
        return true;
    }
    internal bool Unchanged(AuthorityIntent intent)
    {
        foreach(var expected in intent.Cells)
        {
            var actual=Cell(expected.X,expected.Y);
            if(actual.HasTile!=expected.HasTile || actual.HasTile && actual.Type!=expected.Type || actual.Wall!=expected.Wall
                || actual.Half!=expected.Half || actual.Slope!=expected.Slope || actual.Actuated!=expected.Actuated) return false;
            if(intent.Operation=="interact_tile" && Target(intent).Type is TileID.Switches or TileID.Lever
                && (actual.FrameX!=expected.FrameX || actual.FrameY!=expected.FrameY)) return false;
        }
        return true;
    }
    private static void Native(int actor,NativeMutation n)
    {
        using var memory=new MemoryStream(); using var writer=new BinaryWriter(memory);
        writer.Write((byte)n.Message);
        switch(n.Message)
        {
            case 17: writer.Write((byte)n.Action); writer.Write((short)n.X); writer.Write((short)n.Y); writer.Write((short)n.Data); writer.Write((byte)n.Style); break;
            case 19: writer.Write((byte)n.Action); writer.Write((short)n.X); writer.Write((short)n.Y); writer.Write(n.Data==1); break;
            case 31: case 59: writer.Write((short)n.X); writer.Write((short)n.Y); break;
            case 33: writer.Write((short)-1); writer.Write((short)0); writer.Write((short)0); writer.Write((byte)0); break;
            default: throw new FormatException("Unsupported native message");
        }
        var bytes=memory.ToArray(); var buffer=new MessageBuffer { whoAmI=actor };
        try { Array.Copy(bytes,buffer.readBuffer,bytes.Length); buffer.ResetReader(); buffer.GetData(0,bytes.Length,out _); }
        finally { buffer.reader?.Dispose(); buffer.readerStream?.Dispose(); buffer.writer?.Dispose(); buffer.writerStream?.Dispose(); }
    }
}
