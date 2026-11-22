using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace CompanionBridge.ServerIpc;

internal static class ServerDoor
{
    private const int Reach=80;

    internal static string Set(Player p,int x,int y,bool open,out object evidence)
    {
        evidence=null;
        if(p.dead || p.ghost || p.CCed || p.noItems)return "unsupported_actor_state";
        if(!WorldGen.InWorld(x,y,4))return "door_out_of_world";
        Vector2 center=new(x*16+8,y*16+8);
        if(Vector2.DistanceSquared(p.Center,center)>Reach*Reach)return "door_out_of_reach";
        var tile=Main.tile[x,y];
        if(!tile.HasTile || tile.IsActuated || tile.TileType is not (TileID.OpenDoor or TileID.ClosedDoor)
            || tile.TileFrameY is <0 or >=54)return "unsupported_door";
        bool before=tile.TileType==TileID.OpenDoor;
        int top=y-tile.TileFrameY/18;
        int hinge=x;
        if(before)hinge=x-(tile.TileFrameX/18%2)+(tile.TileFrameX/36%2);
        bool State(bool opened)
        {
            for(int row=top;row<top+3;row++)
            {
                var part=Main.tile[hinge,row];
                if(!part.HasTile || part.IsActuated || part.TileType!=(opened?TileID.OpenDoor:TileID.ClosedDoor)
                    || part.TileFrameY!=(row-top)*18)return false;
                if(opened)
                {
                    bool left=part.TileFrameX/36%2==1;
                    var leaf=Main.tile[hinge+(left?-1:1),row];
                    if(!leaf.HasTile || leaf.IsActuated || leaf.TileType!=TileID.OpenDoor
                        || leaf.TileFrameY!=part.TileFrameY || leaf.TileFrameX!=part.TileFrameX+(left?-18:18))return false;
                }
            }
            return true;
        }
        if(!State(before))return "door_geometry_unknown";
        int direction=p.Center.X<center.X?1:-1;
        if(before!=open)
        {
            bool changed=open?WorldGen.OpenDoor(hinge,y,direction):WorldGen.CloseDoor(hinge,y,forced:false);
            if(!changed && open){direction=-direction;changed=WorldGen.OpenDoor(hinge,y,direction);}
            if(!changed)return "door_blocked";
            for (int col = hinge - 1; col <= hinge + 1; col++)
            for (int row = top; row < top + 3; row++)
                SceneMineTargets.Changed(col, row);
            NetMessage.SendTileSquare(-1,hinge-1,top,3,3);
        }
        if(!State(open))return "door_state_unconfirmed";
        evidence=new{x,y,hinge,top,beforeOpen=before,afterOpen=open,changed=before!=open};
        return null;
    }

    // Inspect only the short, unobstructed horizontal corridor to an explicit movement goal.
    internal static bool Ahead(Player p,float goal,out int x,out int y)
    {
        int side=Math.Sign(goal-p.position.X);
        x=(int)(p.Center.X/16);y=(int)(p.Center.Y/16);
        if(side==0 || p.velocity.Y!=0)return false;
        for(int i=0;i<5;i++,x+=side)
        {
            if(!WorldGen.InWorld(x,y,4) || side*(goal+p.width/2-(x*16+8))<=0)return false;
            var t=Main.tile[x,y];
            if(!t.HasTile || t.IsActuated)continue;
            if(t.TileType==TileID.ClosedDoor)return true;
            if(Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType])return false;
        }
        return false;
    }
}
