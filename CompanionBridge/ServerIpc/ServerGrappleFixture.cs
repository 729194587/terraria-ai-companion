using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Only called by the existing copied-world, no-client action fixture.
internal static class ServerGrappleFixture
{
    private static ActorGrapple shot;
    private static readonly List<object> frames = new();
    private static float startY;
    private static int local, mouseX, mouseY;
    private static string sceneMode;
    private static int anchorX,anchorY;
    private static bool disturbed;
    private static int replacement=-1;
    private static bool observeBindingLoss;

    private static bool Enabled=>ServerAiActionFixture.Enabled&&!Terraria.Netplay.HasClients
        &&Program.LaunchParameters.ContainsKey("-companiongrappletest");
    private static string PathFor(string file)=>Path.Combine(Main.SavePath,file);
    internal static void Record(object detail)
    {
        if(!Enabled||sceneMode==null)return;
        var p=ProbeSession.Actor;
        var reused=replacement>=0?Main.projectile[replacement]:null;
        File.AppendAllText(PathFor("grapple-"+sceneMode+".jsonl"),JsonSerializer.Serialize(new
        {
            detail,tick=Main.GameUpdateCount,Main.myPlayer,p.grapCount,
            activeHook=ActorGrapple.HasHook(p),
            replacement=reused==null?null:new{reused.active,reused.owner,reused.identity,reused.type},
            inputsReleased=!p.controlLeft&&!p.controlRight&&!p.controlJump&&!p.controlDown&&!p.controlUp
                &&!p.controlUseItem&&!p.controlUseTile
        })+Environment.NewLine);
    }

    internal static void Tick(Player p)
    {
        if(!Enabled)return;
        string request = Path.Combine(Main.SavePath, "grapple-proof-request");
        string result = Path.Combine(Main.SavePath, "grapple-proof.json");
        string scene=Path.Combine(Main.SavePath,"grapple-scene-request");
        if(shot==null&&!ActorControl.Running&&File.Exists(scene))
        {
            string mode=File.ReadAllText(scene).Trim();
            File.Delete(scene);
            int x=Main.spawnTileX,floor=Main.spawnTileY+8,shelf=floor-10;
            if (mode == "world_journey") x = 48;
            int targetOffset = mode switch { "world_journey" => Main.maxTilesX - 96, "long_journey" => 480, "waypoint" => 16, _ => 5 };
            int shelfEnd = Math.Max(12, targetOffset + 2);
            sceneMode=mode;disturbed=false;replacement=-1;
            anchorX=x+(mode=="diagonal"?1:0);anchorY=shelf-9;
            for(int tx=x-25;tx<=x+Math.Max(25,shelfEnd+4);tx++)
                for(int ty=floor-25;ty<=floor+2;ty++)
                {
                    var tile=Main.tile[tx,ty];
                    tile.ClearEverything();
                    if(ty>=floor||tx==anchorX&&ty==anchorY||tx>=x+2&&tx<=x+shelfEnd&&ty==shelf)
                    {tile.HasTile=true;tile.TileType=TileID.Stone;}
                }
            p.miscEquips[4].SetDefaults(mode switch{"no_hook"=>ItemID.None,"unsupported"=>ItemID.IvyWhip,_=>ItemID.GrapplingHook});
            p.armor[3].SetDefaults(mode=="cloud_hook"?ItemID.CloudinaBottle:ItemID.None);
            p.position=new(x*16-2-(mode=="approach"?96:0),floor*16-p.height);
            p.velocity=Vector2.Zero;
            File.WriteAllText(Path.Combine(Main.SavePath,"grapple-scene-ready.json"),
                JsonSerializer.Serialize(new{x=(x+targetOffset)*16,y=shelf*16,anchorX,anchorY}));
            return;
        }
        if(sceneMode!=null)
        {
            if(observeBindingLoss)
            {
                Record(new{stage="binding_lost",p.velocity.Y});
                observeBindingLoss=false;
            }
            if(replacement>=0)
            {
                var reused=Main.projectile[replacement];
                reused.active=false; // Fixture projectile only.
                replacement=-1;
            }
            if(!disturbed&&sceneMode is "anchor" or "reuse" or "same_reuse" or "cancel" or "miss" or "timeout" or "generation")
            {
                var hook=Main.projectile.FirstOrDefault(q=>q.active&&q.owner==p.whoAmI&&q.type==ProjectileID.Hook);
                bool airborne=hook?.ai[0]==2&&p.velocity.Y< -5;
                bool inFlight=hook?.ai[0]==0&&sceneMode is "miss" or "timeout";
                if(airborne||inFlight)
                {
                    disturbed=true;
                    Record(new{stage="disturbance",mode=sceneMode,airborne,index=hook.whoAmI,vy=p.velocity.Y});
                    switch(sceneMode)
                    {
                        case "anchor": Main.tile[anchorX,anchorY].ClearEverything();break;
                        case "reuse":
                        case "same_reuse":
                            replacement=hook.whoAmI;
                            int identity=hook.identity;
                            bool same=sceneMode=="same_reuse";
                            hook.SetDefaults(same?ProjectileID.Hook:ProjectileID.WoodenArrowFriendly);
                            hook.whoAmI=replacement;hook.owner=same?p.whoAmI:0;hook.identity=same?identity:987;hook.active=true;
                            hook.position=p.Center;hook.velocity=Vector2.Zero;
                            break;
                        case "cancel": File.WriteAllText(PathFor("grapple-cancel-now"),"native pull observed");break;
                        case "miss": hook.ai[0]=1;break;
                        case "timeout": hook.velocity=Vector2.Zero;break;
                        case "generation":
                            // Simulate stale ownership without swapping the reserved slot or touching a human Player.
                            ProbeSession.ActorGeneration++;
                            observeBindingLoss=true;
                            break;
                    }
                }
            }
        }
        if (shot == null)
        {
            if (!File.Exists(request)) return;
            File.Delete(request);
            int x = Main.spawnTileX, floor = Main.spawnTileY + 8, anchor = floor - 12;
            var tile = Main.tile[x, anchor];
            tile.ClearEverything();
            tile.HasTile = true;
            tile.TileType = TileID.Stone;
            p.miscEquips[4].SetDefaults(ItemID.GrapplingHook);
            p.position = new(x * 16 - 2, floor * 16 - p.height);
            p.velocity = Vector2.Zero;
            startY = p.position.Y;
            (local, mouseX, mouseY) = (Main.myPlayer, Main.mouseX, Main.mouseY);
            frames.Clear();
            var identity = new AiReadOnlyProtocol.Identity(p, ProbeSession.WorldSession, ProbeSession.ActorGeneration, p.whoAmI, null);
            shot = ActorGrapple.Fire(identity, ActorGrapple.Selected(p), new Vector2(x * 16 + 8, anchor * 16 + 8));
            if (shot == null) File.WriteAllText(result, "{\"passed\":false,\"reason\":\"fire refused\"}");
            return;
        }

        var q = shot.Active;
        bool globals = local == Main.myPlayer && mouseX == Main.mouseX && mouseY == Main.mouseY;
        frames.Add(new { tick = Main.GameUpdateCount, x = p.position.X, y = p.position.Y, vy = p.velocity.Y,
            owner = q?.owner, index = q?.whoAmI, identity = q?.identity, ai = q?.ai[0], p.grapCount, globals });
        bool pulled = q?.ai[0] == 2 && startY - p.position.Y > 40;
        if (!pulled && globals && q != null && frames.Count < 85) return;
        bool released = shot.Release();
        File.WriteAllText(result, JsonSerializer.Serialize(new { passed = pulled && globals && released && p.grapCount == 0,
            released, owner = shot.Owner, slot = p.whoAmI, shot.Index, shot.Identity, local, frames }));
        shot = null;
    }
}
