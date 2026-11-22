using System;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal static class ServerTileInteraction
{
    internal static bool Available => VirtualPlayerEngineProbe.VirtualPlayerEngineProbe.Engine?.Selected == "2026.09.3.1";

    // Vanilla Lever is a 2x2 object. HitSwitch shifts the horizontal frame bank by 36.
    private static bool Read(int x, int y, out Point origin, out bool on, out string kind)
    {
        kind = null;
        origin = default;
        on = false;
        if (!WorldGen.InWorld(x, y, 4)) return false;
        var tile = Main.tile[x, y];
        int fx = tile.TileFrameX, fy = tile.TileFrameY;
        if (!tile.HasTile || tile.IsActuated) return false;
        origin = new Point(x, y);
        if (tile.TileType is TileID.OpenDoor or TileID.ClosedDoor && fy is 0 or 18 or 36)
        {
            kind = "door";
            on = tile.TileType == TileID.OpenDoor;
            return true;
        }
        if (tile.TileType == TileID.Switches && fy is 0 or 18)
        {
            kind = "switch";
            on = fy == 18;
            return true;
        }
        kind = "lever";
        if (tile.TileType != TileID.Lever || fx is not (0 or 18 or 36 or 54) || fy is not (0 or 18))
            return false;
        on = fx >= 36;
        origin = new Point(x - fx % 36 / 18, y - fy / 18);
        for (int col = 0; col < 2; col++)
        for (int row = 0; row < 2; row++)
        {
            var part = Main.tile[origin.X + col, origin.Y + row];
            if (!part.HasTile || part.IsActuated || part.TileType != TileID.Lever ||
                part.TileFrameX != (on ? 36 : 0) + col * 18 || part.TileFrameY != row * 18)
                return false;
        }
        return true;
    }

    // Vanilla beds use a 4x2 footprint, two orientations, and vertically stacked styles.
    private static bool BedFoot(int x,int y,out Point spawn)
    {
        spawn=default;
        if(!WorldGen.InWorld(x,y,5))return false;
        var tile=Main.tile[x,y];
        int fx=tile.TileFrameX,fy=tile.TileFrameY;
        if(!tile.HasTile || tile.TileType!=TileID.Beds || fx<0 || fx>=144 || fy<0 ||
            fx%18!=0 || fy%18!=0 || !Player.IsHoveringOverABottomSideOfABed(x,y))return false;
        int left=x-fx%72/18,top=y-fy/18%2;
        for(int col=0;col<4;col++)
        for(int row=0;row<2;row++)
        {
            var part=Main.tile[left+col,top+row];
            if(!part.HasTile || part.IsActuated || part.TileType!=TileID.Beds ||
                part.TileFrameX!=fx/72*72+18*col || part.TileFrameY!=fy/36*36+18*row)return false;
        }
        spawn=new Point(left+(fx>=72?1:2),top+2);
        return WorldGen.InWorld(spawn.X,spawn.Y,10);
    }

    internal static object Observe(AiReadOnlyProtocol.Identity id, int x, int y)
    {
        if(Available && BedFoot(x,y,out var spawn))
        {
            var p=(Player)id.Actor;
            return new {kind="bed",active=p.SpawnX==spawn.X && p.SpawnY==spawn.Y,
                target=SceneMineTargets.Observe(id,x,y)};
        }
        return Available && Read(x, y, out _, out bool on, out var kind)
            ? new { kind, active = on, target = SceneMineTargets.Observe(id, x, y) }
            : null;
    }

    internal static Result Activate(AiReadOnlyProtocol.Identity id, JsonElement args)
    {
        Result Refuse(string reason) => new("failed", reason, null, true, InputsAcquired: false);
        var actor = (Player)id.Actor;
        if (actor.CCed || actor.noItems) return Refuse("unsupported_actor_state");
        string stale = SceneMineTargets.Resolve(id, args, out int x, out int y);
        if (stale != null) return Refuse(stale);
        if (Vector2.DistanceSquared(actor.Center, new Vector2(x * 16 + 8, y * 16 + 8)) > 80 * 80)
            return Refuse("interaction_out_of_reach");
        if(BedFoot(x,y,out var spawn))
        {
            if(!actor.IsInTileInteractionRange(x,y,Terraria.DataStructures.TileReachCheckSettings.Simple))
                return Refuse("interaction_out_of_reach");
            actor.FindSpawn();
            int oldX=actor.SpawnX,oldY=actor.SpawnY;
            bool wasActive=oldX==spawn.X && oldY==spawn.Y;
            if(!wasActive && !Player.CheckSpawn(spawn.X,spawn.Y))return Refuse("invalid_bed_spawn");
            try
            {
                if(wasActive)actor.RemoveSpawn();
                else actor.ChangeSpawn(spawn.X,spawn.Y);
                actor.FindSpawn();
                if(actor.SpawnX!=(wasActive?-1:spawn.X) || actor.SpawnY!=(wasActive?-1:spawn.Y))
                    return Unknown();
                return new("completed","bed_spawn_verified",new{x,y,kind="bed",beforeActive=wasActive,
                    afterActive=!wasActive,bedSpawnX=spawn.X,bedSpawnY=spawn.Y,beforeSpawnX=oldX,beforeSpawnY=oldY,
                    afterSpawnX=actor.SpawnX,afterSpawnY=actor.SpawnY},true,true,InputsAcquired:false);
            }
            catch(Exception error){return Unknown(error.GetType().Name);}
            finally{SceneMineTargets.Changed(x,y);}
        }
        if (!Read(x, y, out var origin, out bool before, out var kind)) return Refuse("unsupported_interaction");
        if (kind == "door")
        {
            string reason = ServerDoor.Set(actor, x, y, !before, out _);
            if (reason == "door_state_unconfirmed") return Unknown();
            if (reason != null) return Refuse(reason);
        }
        else
        {
            Wiring.SetCurrentUser(actor.whoAmI);
            try { Wiring.HitSwitchAndSync(x, y); }
            catch (Exception error) { return Unknown(error.GetType().Name); }
            finally
            {
                Wiring.SetCurrentUser(-1);
                int size = kind == "lever" ? 2 : 1;
                for (int col = 0; col < size; col++)
                for (int row = 0; row < size; row++)
                    SceneMineTargets.Changed(origin.X + col, origin.Y + row);
            }
            if (!Read(x, y, out var afterOrigin, out bool after, out var afterKind)
                || afterOrigin != origin || afterKind != kind || after == before) return Unknown();
        }
        return new("completed", "interaction_verified", new { x, y, kind, beforeActive = before, afterActive = !before },
            true, true, InputsAcquired: false);

        Result Unknown(string detail = null) => new("unknown", "interaction_effect_unconfirmed", null,
            true, InputsAcquired: false, ReasonDetail: detail);
    }
}
