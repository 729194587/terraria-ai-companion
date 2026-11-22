using System;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.Tile_Entities;
using Terraria.ID;
using Terraria.ModLoader;

namespace CompanionBridge.ServerIpc;

// Loaded only by the isolated copied-world pylon test. Exercises tML's actual GlobalPylon callback.
public sealed class PylonDiagnostic : GlobalPylon
{
    private static int x,y;
    private static string mode;
    public override bool IsLoadingEnabled(Mod mod) => Main.dedServ && ServerAiActionFixture.Enabled
        && Program.LaunchParameters.ContainsKey("-companionpylontest");

    internal static object Setup(string scene, Player actor)
    {
        mode=scene;
        if(scene=="pylon-create")
        {
            x=Math.Clamp((int)(actor.Center.X/16)+120,100,Main.maxTilesX-100);
            y=Main.spawnTileY-8;
            for(int dx=0;dx<3;dx++)
            for(int dy=0;dy<=4;dy++)
            {
                var tile=Main.tile[x+dx,y+dy];
                tile.ClearEverything();
                tile.HasTile=true;
                tile.TileType=dy==4?TileID.Dirt:TileID.TeleportationPylon;
                tile.TileFrameX=(short)(dy==4?0:dx*18 + 54*(int)TeleportPylonType.Victory);
                tile.TileFrameY=(short)(dy==4?0:dy*18);
            }
            TETeleportationPylon.Place(x,y);
            Main.PylonSystem.RequestImmediateUpdate();
        }
        if(scene=="pylon-remove")
        {
            TETeleportationPylon.Kill(x,y);
            for(int dx=0;dx<3;dx++)
            for(int dy=0;dy<4;dy++) Main.tile[x+dx,y+dy].ClearEverything();
            Main.PylonSystem.RequestImmediateUpdate();
        }
        return new { x,y,actorX=actor.position.X };
    }

    public override void PostValidTeleportCheck(TeleportPylonInfo destination, TeleportPylonInfo nearby,
        ref bool destinationValid, ref bool nearbyValid, ref string errorKey)
    {
        if(destination.PositionInTiles.X!=x || destination.PositionInTiles.Y!=y) return;
        if(mode=="pylon-allow") destinationValid=nearbyValid=true;
        if(mode=="pylon-deny")
        {
            destinationValid=false;
            errorKey="CompanionFixture.CustomPylonDenial";
        }
    }
}
