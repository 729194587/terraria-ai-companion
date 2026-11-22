using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal static class ServerTorchFixture
{
    internal static bool Enabled=>ServerAiActionFixture.Enabled&&Program.LaunchParameters.ContainsKey("-companiontorchtest");
    internal static bool Hold=>Enabled&&File.Exists(Path.Combine(Main.SavePath,"torch-hold"));
    internal static bool Paused=>Enabled&&File.Exists(Path.Combine(Main.SavePath,"torch-pause"));
    internal static void Record(object evidence)
    {
        if(Enabled)File.AppendAllText(Path.Combine(Main.SavePath,"torch-placement.jsonl"),
            JsonSerializer.Serialize(evidence)+System.Environment.NewLine);
    }
    internal static void Tick(Player p)
    {
        if(!Enabled)return;
        string request=Path.Combine(Main.SavePath,"torch-request");
        if(!File.Exists(request))return;
        string mode=File.ReadAllText(request).Trim();
        File.Delete(request);
        int x=Main.spawnTileX,y=(int)Main.rockLayer+30;
        if(mode=="daylight")
        {
            y=(int)Main.worldSurface-18;
            Main.dayTime=true;Main.time=27000;
        }
        if(mode=="stock")
        {
            p.inventory[15].SetDefaults(ItemID.Torch);p.inventory[15].stack=4;
            NetworkStateSync.Full(p);
        }
        else
        {
            for(int tx=x-50;tx<=x+60;tx++)for(int ty=y-33;ty<=y+32;ty++)
            {
                var t=Main.tile[tx,ty];t.ClearEverything();
                if(ty>=y||mode!="daylight"&&ty<=y-9||mode=="occluded"&&tx>=x-6&&tx<=x-3)
                {t.HasTile=true;t.TileType=TileID.Stone;}
                if(mode=="blocked"&&tx>=x-3&&tx<=x+3&&ty==y-1)
                {t.HasTile=true;t.TileType=TileID.Rope;}
            }
            if(mode is "lit" or "occluded")
            {
                var t=Main.tile[mode=="lit"?x+2:x-7,y-1];
                t.HasTile=true;t.TileType=TileID.Torches;t.TileFrameX=0;t.TileFrameY=0;
            }
            if(mode=="gemspark")
            {
                var t=Main.tile[x+2,y];
                t.TileType=TileID.DiamondGemspark;
            }
            if(mode=="mine")
            {
                var t=Main.tile[x+11,y-2];t.HasTile=true;t.TileType=TileID.Stone;
            }
            p.position=new(x*16,y*16-p.height);p.velocity=Vector2.Zero;
            p.selectedItem=0;p.statLife=p.statLifeMax2;
            for(int i=0;i<50;i++)p.inventory[i].TurnToAir();
            p.inventory[0].SetDefaults(ItemID.WoodenSword);
            p.inventory[1].SetDefaults(ItemID.CopperPickaxe);
            if(mode is not ("empty" or "nav" or "mine"))
            {p.inventory[15].SetDefaults(ItemID.Torch);p.inventory[15].stack=mode=="single"?1:4;}
            NetworkStateSync.Full(p);
        }
        File.WriteAllText(Path.Combine(Main.SavePath,"torch-ready.json"),JsonSerializer.Serialize(new{
            mode,x,y,gameUpdate=Main.GameUpdateCount,activeAgent=ServerAiSystem.AgentActive}));
    }
}
