using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal static class ServerRopeFixture
{
    private static string mode;
    private static (int Local,int MouseX,int MouseY,int X,int Y) cursor;
    private static bool injected;
    private static bool Enabled=>ServerAiActionFixture.Enabled&&!Netplay.HasClients
        &&Program.LaunchParameters.ContainsKey("-companionropetest");

    internal static void Tick(Player p)
    {
        if(!Enabled)return;
        string request=Path.Combine(Main.SavePath,"rope-request");
        if(!File.Exists(request))
        {
            if(!injected&&(mode is "timeout" or "generation")&&ActorItemUse.Running
                &&ActorItemUse.Current is {} use&&ReferenceEquals(use.Actor,p)&&use.Placement?.Rope!=null&&use.Ticks==0)
            {
                injected=true;
                if(mode=="timeout")use.Ticks=600;
                else ProbeSession.ActorGeneration++;
                File.WriteAllText(Path.Combine(Main.SavePath,"rope-lifecycle-"+mode+".json"),
                    JsonSerializer.Serialize(new { stack=use.Item.stack,empty=!Main.tile[use.Placement.X,use.Placement.Y].HasTile,
                        inputReleased=!p.controlUseItem,ProbeSession.ActorGeneration }));
            }
            return;
        }
        mode=File.ReadAllText(request).Trim();File.Delete(request);
        injected=false;
        int center=Main.spawnTileX,x=center+2,floor=Main.spawnTileY+8,y=floor-3;
        int ceiling=floor-(mode=="upward"?20:10);
        for(int tx=center-8;tx<=center+8;tx++)
            for(int ty=ceiling;ty<=floor+7;ty++)
            {
                var tile=Main.tile[tx,ty];tile.ClearEverything();
                if(ty>=floor){tile.HasTile=true;tile.TileType=TileID.Stone;}
            }
        if(mode=="upward")
        {
            // Leave an empty cell at both ends of the seeded rope. A mistaken
            // downward retarget could then succeed and be detected by the test.
            for(int ty=y;ty<floor;ty++)
            {
                var segment=Main.tile[x,ty];
                segment.HasTile=true;segment.TileType=TileID.Rope;
            }
            Main.tile[x,floor].ClearEverything();
        }
        else if(mode!="build_and_climb")
        {
            var anchor=Main.tile[x,y-1];
            anchor.HasTile=true;anchor.TileType=TileID.Stone;
        }
        if(mode=="end_range")
        {
            for(int ty=y;ty<=floor+11;ty++)
            {var tile=Main.tile[x,ty];tile.ClearEverything();tile.HasTile=true;tile.TileType=TileID.Rope;}
            Main.tile[x,floor+12].ClearEverything();
        }
        p.position=new(mode=="build_and_climb"?x*16-2:center*16,floor*16-p.height);
        p.velocity=Vector2.Zero;
        p.selectedItem=0;
        p.inventory[4].SetDefaults(mode switch
        {
            "empty"=>ItemID.None,"vine"=>ItemID.VineRope,"silk"=>ItemID.SilkRope,"web"=>ItemID.WebRope,_=>ItemID.Rope
        });
        if(!p.inventory[4].IsAir)p.inventory[4].stack=mode switch
        {
            "upward"=>16,"build_and_climb"=>10,"vine" or "silk" or "web"=>1,_=>5
        };
        PlacementSelfTest.DenyTile=mode=="refuse";
        cursor=(Main.myPlayer,Main.mouseX,Main.mouseY,Player.tileTargetX,Player.tileTargetY);
        // Native PlaceThing_Tiles tests the fractional upper bound before any
        // rope retarget. Ceiling gives the first integer row inside that bound.
        int minReachY=(int)System.Math.Ceiling(p.position.Y/16-Player.tileRangeY-p.inventory[4].tileBoost-p.blockRange);
        File.WriteAllText(Path.Combine(Main.SavePath,"rope-ready.json"),JsonSerializer.Serialize(new {x,y,floor,slot=p.whoAmI,minReachY}));
    }

    internal static void ObservePlacementScope(int x,int y)
    {
        if(!Enabled||ActorItemUse.Current?.Placement?.Rope==null)return;
        var human=Main.player[0];
        bool unchanged=cursor==(Main.myPlayer,Main.mouseX,Main.mouseY,Player.tileTargetX,Player.tileTargetY);
        bool unscoped=ActorPlacement.TargetX(human)==Player.tileTargetX&&ActorPlacement.TargetY(human)==Player.tileTargetY;
        File.AppendAllText(Path.Combine(Main.SavePath,"rope-scope.jsonl"),
            JsonSerializer.Serialize(new {mode,x,y,unchanged,unscoped})+System.Environment.NewLine);
    }
}
