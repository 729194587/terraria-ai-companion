using System;
using Terraria;
using Terraria.ID;

namespace VirtualPlayerEngineProbe;

// Isolated copied-world fixture only; state injection never used by production actions.
internal static class AiMapNativeTest
{
    internal static void Run(Player p,Action<bool,string> check)
    {
        var original=p.position;var map=AiMapRuntime.Map;
        int cx=Main.maxTilesX/2,cy=(int)Main.worldSurface+80;
        for(int x=cx-62;x<=cx+62;x++)for(int y=cy-36;y<=cy+36;y++)Main.tile[x,y].ClearEverything();
        void Move(int x,int y){p.Center=new(x*16+8,y*16+8);AiMapRuntime.Tick(true);}
        try
        {
            Move(cx,cy);
            check(map.Get(cx,cy)==1&&map.Get(cx+10,cy)==0,"underground rectangle revealed without illumination");
            var torch=Main.tile[cx+2,cy];torch.HasTile=true;torch.TileType=TileID.Torches;torch.TileFrameX=0;
            Move(cx,cy);check(map.Get(cx+10,cy)==1,"torch did not enlarge underground reveal");
            map=AiMapRuntime.Map=new AiMap(Guid.NewGuid(),"native-occlusion",Main.maxTilesX,Main.maxTilesY);
            for(int y=cy-34;y<=cy+33;y++){var t=Main.tile[cx+5,y];t.HasTile=true;t.TileType=TileID.Stone;}
            Move(cx,cy);check(map.Get(cx+5,cy)==2&&map.Get(cx+8,cy+3)==0,"opaque wall did not block unseen cells");
            var stone=Main.tile[cx-1,cy];stone.HasTile=true;stone.TileType=TileID.Stone;
            Move(cx,cy);check(map.Get(cx-1,cy)==2,"re-observation did not resample");
            Move(cx+200,cy);check(map.Get(cx+100,cy)==0,"teleport revealed intermediate path");
            Main.tile[cx-1,cy].ClearEverything();
            check(map.Get(cx-1,cy)==2,"remote human mutation changed memory");
            p.dead=true;Move(cx+400,cy);check(map.Get(cx+400,cy)==0&&map.Get(cx,cy)==1,"death erased/revealed map");
            p.dead=false;Move(cx,cy);check(map.Get(cx-1,cy)==1,"respawn/revisit did not retain and refresh memory");
        }
        finally{p.dead=false;p.position=original;}
    }
}
