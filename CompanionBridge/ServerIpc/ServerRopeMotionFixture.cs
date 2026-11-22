using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal static class ServerRopeMotionFixture
{
    private static string disturbance;
    internal static void Tick(Player p)
    {
        if(!Program.LaunchParameters.ContainsKey("-companionropemotiontest"))return;
        string path=Path.Combine(Main.SavePath,"rope-motion-request");
        int x=Main.spawnTileX,floor=Main.spawnTileY+8;
        if(File.Exists(path))
        {
            string mode=File.ReadAllText(path).Trim();
            File.Delete(path);
            if(mode=="exit")Main.tile[x,floor-4].ClearTile();
            else if(mode is "break" or "generation")disturbance=mode;
            else
            {
                disturbance=null;
                for(int tx=x-4;tx<=x+4;tx++)
                    for(int y=floor-24;y<=floor+2;y++)
                    {
                        var tile=Main.tile[tx,y];tile.ClearEverything();
                        if(y>=floor){tile.HasTile=true;tile.TileType=TileID.Stone;}
                        else if(tx==x&&y>=floor-20&&y<=floor-4){tile.HasTile=true;tile.TileType=TileID.Rope;}
                    }
                if(mode=="blocked")
                {
                    var tile=Main.tile[x+1,floor-8];
                    tile.HasTile=true;tile.TileType=TileID.Stone;
                }
                p.position=new(x*16-2,floor*16-p.height);
                p.velocity=Vector2.Zero;
            }
            File.WriteAllText(Path.Combine(Main.SavePath,"rope-motion-ready.json"),JsonSerializer.Serialize(new{x,floor=floor*16}));
        }
        if(disturbance!=null&&ActorControl.Driver is ServerAiRope driver&&driver.Running
            &&driver.Matches(p)&&driver.Current.AttachedObserved&&driver.Current.Updates>=5)
        {
            if(disturbance=="break")Main.tile[x,(int)((p.position.Y-8)/16)-2].ClearTile();
            else ProbeSession.ActorGeneration++;
            File.WriteAllText(Path.Combine(Main.SavePath,"rope-motion-"+disturbance+".json"),
                JsonSerializer.Serialize(new{p.pulley,feet=p.Bottom.Y,inputsReleased=!p.controlUp&&!p.controlDown,
                    Main.myPlayer,slot=p.whoAmI}));
            disturbance=null;
        }
    }
}
