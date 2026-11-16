using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using VirtualPlayerEngineProbe;

using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;

namespace CompanionBridge.ServerIpc;

// Only the isolated, copied-world harness can seed a target. All attacks run in natural frames.
internal static class ServerCombatFixture
{
    internal static bool Enabled => ServerAiActionFixture.Enabled && Program.LaunchParameters.ContainsKey("-companioncombattest");
    private static NPC target;
    private static readonly HashSet<NPC> defenseTargets=new();
    private static ulong? lastFrame,lastHit;
    private static int frames,hits,hitViolations;
    private static bool faultNextPress;
    private static int presses,spawnPackets,ammoPackets;
    private static int? firstCooldown;
    private static readonly List<object> hitTrace=new();
    private static readonly List<object> shots=new(),projectileHits=new(),flightTrace=new();
    internal static void ObserveProjectileSpawn(Projectile projectile,NativeCombatWorld.Shot shot)
    {
        if(!Enabled)return;
        var stack=new System.Diagnostics.StackTrace().ToString();
        shots.Add(new{tick=Main.GameUpdateCount,shot.Owner,shot.Identity,shot.Weapon,shot.Ammo,shot.Verified,
            shot.Type,shot.Slot,x=projectile.Center.X,y=projectile.Center.Y,tx=Main.npc[shot.Target.Slot].Center.X,ty=Main.npc[shot.Target.Slot].Center.Y,
            targetVx=Main.npc[shot.Target.Slot].velocity.X,targetVy=Main.npc[shot.Target.Slot].velocity.Y,target=shot.Target.EntityRef,vx=projectile.velocity.X,vy=projectile.velocity.Y,
            nativeShoot=stack.Contains("ItemCheck_Shoot"),nativeWrapper=stack.Contains("Terraria.Player::ItemCheckWrapped")});
    }
    internal static void ObserveProjectileHit(Projectile p,NPC n,int damage,NativeCombatWorld.Shot shot)
    {
        if(!Enabled)return;
        var stack=new System.Diagnostics.StackTrace().ToString();
        projectileHits.Add(new{tick=Main.GameUpdateCount,p.identity,p.owner,n.life,damage,launched=shot.Launched,
            target=NativeCombatWorld.Reference(Bound(ProbeSession.Actor),n),
            nativeDamage=stack.Contains("Projectile::Damage")||stack.Contains("Projectile:Damage")||stack.Contains("Projectile.Damage")||stack.Contains("NativeSurvival.ProjectileDamage")});
    }
    private static Identity Bound(Player p)=>new(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,p.whoAmI,null);
    private static int Cooldown(Player p,int slot)=>
        ((int[])typeof(Player).GetField("meleeNPCHitCooldown",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)
            .GetValue(p))[slot];

    internal static void ObserveFlight(Projectile p,NativeCombatWorld.Shot shot)
    {
        if(!Enabled||shots.Count!=1||flightTrace.Count>=60)return;
        flightTrace.Add(new{tick=Main.GameUpdateCount,p.active,x=p.Center.X,y=p.Center.Y,
            vx=p.velocity.X,vy=p.velocity.Y,p.damage,ai=p.ai[0],targetX=target.Center.X,targetY=target.Center.Y});
    }

    internal static void Install(Action<Action,Action> install)
    {
        if(!Program.LaunchParameters.ContainsKey("-companioncombattest"))return;
        install(()=>On_Player.ItemCheck+=ObserveItemFrame,()=>On_Player.ItemCheck-=ObserveItemFrame);
        install(()=>On_NetMessage.SendData+=ObservePackets,()=>On_NetMessage.SendData-=ObservePackets);
    }

    private static void ObservePackets(On_NetMessage.orig_SendData orig,int msg,int remote,int ignore,NetworkText text,
        int number,float a,float b,float c,int d,int e,int f)
    {
        orig(msg,remote,ignore,text,number,a,b,c,d,e,f);
        var p=ProbeSession.Actor;
        if(p==null||!Enabled||!NativeCombatWorld.AllowsItem(p))return;
        if(msg==MessageID.SyncProjectile&&NativeItemInput.Active(p)
            &&Main.projectile[number].TryGetGlobalProjectile<CombatProjectile>(out var trace)&&trace.Flight is {Verified:true})
            spawnPackets++;
        if(msg==MessageID.SyncEquipment&&number==p.whoAmI&&a==54)ammoPackets++;
    }

    private static void ObserveItemFrame(On_Player.orig_ItemCheck orig,Player p)
    {
        if(!Enabled||!NativeCombatWorld.AllowsItem(p)){orig(p);return;}
        ulong tick=Main.GameUpdateCount;
        if(lastFrame==tick||!NativeItemInput.Active(p)||Main.myPlayer!=p.whoAmI||Main.netMode!=NetmodeID.Server)
            throw new InvalidOperationException("Combat fixture observed duplicate or unscoped native ItemCheck.");
        lastFrame=tick;frames++;
        if(frames==1)firstCooldown=Cooldown(p,target.whoAmI);
        if(p.controlUseItem)presses++;
        if(faultNextPress&&p.controlUseItem)
        {
            faultNextPress=false;
            // tML catches ModPlayer callback faults; inject at the native
            // boundary so this diagnostic exercises the wrapper unwind.
            throw new InvalidOperationException("Intentional native combat input fault.");
        }
        orig(p);
    }

    internal static void ObserveHit(Player p,NPC npc,int damageDone)
    {
        if(!Enabled||!NativeCombatWorld.AllowsItem(p))return;
        ulong tick=Main.GameUpdateCount;
        if(lastHit==tick||lastFrame!=tick||!Keeps(npc)||!NativeItemInput.Active(p))
            hitViolations++;
        lastHit=tick;hits++;
        var stack=new System.Diagnostics.StackTrace().ToString();
        hitTrace.Add(new{tick,press=presses,npc.whoAmI,npc.life,damageDone,entityRef=NativeCombatWorld.Reference(Bound(p),npc),
            cooldown=Cooldown(p,npc.whoAmI),nativeWrapper=stack.Contains("Terraria.Player::ItemCheckWrapped"),
            nativeDamage=stack.Contains("ProcessHitAgainstNPC")});
    }
    internal static bool Keeps(NPC npc) => Enabled && (ReferenceEquals(npc,target)||defenseTargets.Contains(npc));
    internal static void RecordDeath(NPC npc)
    {
        if(!Keeps(npc))return;
        File.AppendAllText(Path.Combine(Main.SavePath,"combat-deaths.jsonl"),
            JsonSerializer.Serialize(new{gameUpdate=Main.GameUpdateCount,npc.whoAmI,npc.life,
                stack=new System.Diagnostics.StackTrace().ToString()})+Environment.NewLine);
    }

    private static NPC SpawnDefense(Player p,int dx,int life,bool threatening=true,int start=0)
    {
        int floor=Main.spawnTileY+8,x=(int)p.Center.X+dx;
        int slot=NPC.NewNPC(p.GetSource_Misc("isolated_defense_fixture"),x,floor*16,NPCID.GreenSlime,
            Start:start,Target:threatening?p.whoAmI:255);
        var n=Main.npc[slot];
        n.Bottom=new Vector2(x,floor*16);
        n.target=threatening?p.whoAmI:255;n.aiStyle=-1;n.knockBackResist=0;n.life=n.lifeMax=life;n.timeLeft=1800;
        defenseTargets.Add(n);
        return n;
    }

    private static object DefenseIdentity(Player p,NPC n)=>new{slot=n.whoAmI,n.life,entityRef=NativeCombatWorld.Reference(Bound(p),n)};

    internal static void Tick(Player p)
    {
        if(!Enabled)return;
        // The copied world can contain town NPCs whose attacks would decide
        // the fight. Keep the arena isolated from ambient friendly projectiles.
        foreach(var projectile in Main.projectile)
            if(projectile.active&&projectile.friendly&&(!projectile.TryGetGlobalProjectile<CombatProjectile>(out var trace)
                ||trace.Flight is not {Verified:true}))projectile.active=false;
        foreach(var npc in Main.npc)
            if(npc.active&&npc.townNPC)npc.active=false;
        var request=Path.Combine(Main.SavePath,"combat-request");
        if(!File.Exists(request))return;
        string mode;
        try
        {
            mode=File.ReadAllText(request).Trim();
            File.Delete(request);
        }
        catch(IOException){return;} // Windows can briefly lock the atomically renamed request.
        if(mode is "audit" or "fault")
        {
            if(mode=="fault")faultNextPress=true;
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                JsonSerializer.Serialize(new{mode,gameUpdate=Main.GameUpdateCount,frames,hits,hitViolations,firstCooldown,hitTrace,shots,projectileHits,flightTrace,spawnPackets,ammoPackets,ammo=p.inventory[54].stack,spareAmmo=p.inventory[53].stack,
                    survivors=defenseTargets.Where(n=>n.active).Select(n=>DefenseIdentity(p,n)),inputScopeReleased=!NativeItemInput.Active(p)&&Main.myPlayer!=p.whoAmI,
                    attackReleased=!p.controlUseItem&&p.releaseUseItem}));
            return;
        }
        if(mode=="ranged-clearance")
        {
            int slitX=(int)(p.Center.X/16)+12,y=(int)(p.Center.Y/16)-6;
            for(int dx=-2;dx<=2;dx++)
                for(int dy=-2;dy<=2;dy++)Main.tile[slitX+dx,y+dy].ClearEverything();
            for(int dx=0;dx<=1;dx++)
            {
                var top=Main.tile[slitX+dx,y-1];top.HasTile=true;top.TileType=TileID.Stone;
                var bottom=Main.tile[slitX+dx,y+1];bottom.HasTile=true;bottom.TileType=TileID.Stone;
            }
            var arrow=CombatBallistics.Profile(ProjectileID.WoodenArrowFriendly);
            var bullet=CombatBallistics.Profile(ProjectileID.Bullet);
            var flags=(Collision.up,Collision.down,Collision.stair,Collision.stairFall,Collision.sloping,
                Collision.honey,Collision.shimmer,Collision.landMine);
            // Enter a one-tile slit at its center and at a near-edge height
            // where the retired 4x4 footprint fit, but the actual arrow clips.
            bool Sweep(int dy,CombatBallistics.CollisionProfile profile)=>CombatBallistics.ClearStep(
                new Vector2(slitX*16-6,y*16+dy),new Vector2(12,0),profile);
            bool centered=Sweep(8,arrow),edge=Sweep(3,arrow),legacy=Sweep(3,arrow with{Width=4,Height=4});
            bool restored=flags==(Collision.up,Collision.down,Collision.stair,Collision.stairFall,Collision.sloping,
                Collision.honey,Collision.shimmer,Collision.landMine);
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                JsonSerializer.Serialize(new{arrow.Width,arrow.Height,bulletWidth=bullet.Width,bulletHeight=bullet.Height,centered,edge,legacy,restored}));
            return;
        }
        if(mode=="defense-join")
        {
            var added=SpawnDefense(p,24,45);
            var bystander=SpawnDefense(p,-96,45,threatening:false);
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                JsonSerializer.Serialize(new{added=DefenseIdentity(p,added),bystander=DefenseIdentity(p,bystander)}));
            return;
        }
        if(mode=="defense-replace")
        {
            int oldSlot=target.whoAmI;
            target.StrikeNPC(new NPC.HitInfo{Damage=target.life});
            target=SpawnDefense(p,-24,45,start:oldSlot);
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(DefenseIdentity(p,target)));
            return;
        }
        if(mode=="ranged-replace")
        {
            var old=target;
            int oldSlot=old.whoAmI;
            var bottom=old.Bottom;
            bool live=Main.projectile.Any(projectile=>projectile.active&&projectile.TryGetGlobalProjectile<CombatProjectile>(out var trace)
                &&trace.Flight?.Target.Slot==oldSlot);
            old.active=false;
            target=SpawnDefense(p,-300,32,threatening:false,start:oldSlot);
            target.Bottom=bottom;
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(new{
                slot=target.whoAmI,entityRef=NativeCombatWorld.Reference(Bound(p),target),target.life,live}));
            return;
        }
        if(mode=="replace")
        {
            var old=target.whoAmI;
            var bottom=target.Bottom;
            target.active=false;
            int replacement=NPC.NewNPC(p.GetSource_Misc("isolated_combat_replacement"),(int)bottom.X,(int)bottom.Y,
                target.type,Start:old,Target:255);
            target=Main.npc[replacement];
            target.Bottom=bottom;target.aiStyle=-1;target.target=255;
            var bound=new AiReadOnlyProtocol.Identity(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,p.whoAmI,null);
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                JsonSerializer.Serialize(new{mode,slot=replacement,target.active,entityRef=NativeCombatWorld.Reference(bound,target)}));
            return;
        }
        if(mode is "external" or "despawn")
        {
            if(target?.active!=true)throw new InvalidOperationException("Combat fixture target is unavailable.");
            if(mode=="external")target.StrikeNPC(new NPC.HitInfo{Damage=target.life});
            else target.active=false;
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                JsonSerializer.Serialize(new{mode,target.active,target.life,gameUpdate=Main.GameUpdateCount}));
            return;
        }
        if(defenseTargets.Count>0){p.immune=false;p.immuneTime=0;}
        foreach(var n in defenseTargets)n.active=false;
        defenseTargets.Clear();
        bool durable=mode is "agent-multi" or "durable" or "reuse";
        bool distant=mode is "agent" or "agent-multi";
        bool stationary=durable||distant;
        int startSlot=mode=="reflex"?10:0;
        if(mode=="reuse")
        {
            if(target==null||target.active||target.life>0)
                throw new InvalidOperationException("Reuse requires the preceding defeated NPC.");
            startSlot=target.whoAmI;
        }
        lastFrame=lastHit=null;frames=hits=hitViolations=presses=spawnPackets=ammoPackets=0;faultNextPress=false;
        firstCooldown=null;hitTrace.Clear();shots.Clear();projectileHits.Clear();flightTrace.Clear();
        if(distant&&(p.HeldItem.type==ItemID.WoodenSword||CombatBallistics.Supported(p.HeldItem)))
        {
            p.HeldItem.SetDefaults(ItemID.PlatinumBroadsword);
            p.statLife=p.statLifeMax2;
        }
        if(target?.active==true)target.active=false;
        int floor=Main.spawnTileY+8;
        // Keep the arena floor under the native knockback travel.
        int center=Main.spawnTileX;
        for(int tx=center-75;tx<=center+75;tx++)
        for(int ty=floor-12;ty<=floor+2;ty++)
        {
            var tile=Main.tile[tx,ty];
            tile.ClearEverything();
            if(ty>=floor){tile.HasTile=true;tile.TileType=TileID.Dirt;}
        }
        if(mode is "bow" or "gun" or "bow-reflex" or "bow-empty" or "bow-one" or "bow-wall" or "bow-high" or "bow-sealed")
        {
            bool gun=mode=="gun",reflex=mode=="bow-reflex";
            p.HeldItem.SetDefaults(gun?ItemID.Minishark:ItemID.WoodenBow);
            p.inventory[54].SetDefaults(gun?ItemID.MusketBall:ItemID.WoodenArrow);
            p.inventory[54].stack=24;
            p.inventory[53].SetDefaults(p.inventory[54].type);p.inventory[53].stack=4;
            if(mode=="bow-empty"){p.inventory[54].TurnToAir();p.inventory[53].TurnToAir();}
            bool single=mode=="bow-one";
            if(single){p.inventory[54].stack=1;p.inventory[53].TurnToAir();}
            target=SpawnDefense(p,reflex?-95:single?-320:-200,gun?70:32,threatening:reflex);
            void Wall(int tx,int ty)
            {var tile=Main.tile[tx,ty];tile.HasTile=true;tile.TileType=TileID.Stone;}
            if(mode=="bow-wall")
            {
                int wall=(int)(p.Center.X/16)-4;
                for(int dx=-1;dx<=0;dx++)
                    for(int dy=1;dy<=3;dy++)Wall(wall+dx,floor-dy);
            }
            if(mode=="bow-high"){target.noGravity=true;target.position.Y-=80;}
            if(mode=="bow-sealed")
            {
                int tx=(int)(target.Center.X/16);
                target.Center=new Vector2(tx*16+8,target.Center.Y);
                for(int dx=-2;dx<=2;dx++)
                    for(int dy=1;dy<=3;dy++)
                        if(Math.Abs(dx)==2||dy==3)Wall(tx+dx,floor-dy);
            }
            bool initialAim=CombatBallistics.ReadRound(p,p.HeldItem,out var round)==null
                &&CombatBallistics.Aim(p,target,p.HeldItem,round,out _);
            if(gun){target.noGravity=true;target.velocity=new Vector2(.6f,-.25f);}
            if(mode=="bow")SpawnDefense(p,-105,40,threatening:false);
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                JsonSerializer.Serialize(new{slot=target.whoAmI,entityRef=NativeCombatWorld.Reference(Bound(p),target),
                    ammo=p.inventory[54].stack,spareAmmo=p.inventory[53].stack,ammoType=p.inventory[54].type,weapon=p.HeldItem.type,initialAim,startX=p.Center.X,startY=p.Center.Y}));
            return;
        }
        if(mode=="defense")
        {
            // These fixtures exercise targeting threats; isolate contact knockback
            // so static NPCs cannot leave the threat radius by pushing the actor.
            p.immune=true;p.immuneTime=600;
            target=SpawnDefense(p,-26,90);
            var second=SpawnDefense(p,-52,45);
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                JsonSerializer.Serialize(new{first=DefenseIdentity(p,target),second=DefenseIdentity(p,second)}));
            return;
        }
        // Leave enough room for residual native movement after explicit cancellation.
        int x=(int)p.Center.X+(distant?-180:durable?-32:-24);
        // Keep the reflex spawn on a different slot from the preceding kill,
        // whose native per-slot melee cooldown can still be active.
        int slot=NPC.NewNPC(p.GetSource_Misc("isolated_combat_fixture"),x,floor*16,NPCID.GreenSlime,
            Start:startSlot,
            Target:stationary?255:p.whoAmI);
        target=Main.npc[slot];
        if(stationary)
        {
            target.aiStyle=-1;
            target.knockBackResist=0;
            target.timeLeft=1800;
        }
        else
        {
            // Start in the native grounded pause between jumps so startup
            // and IPC scheduling do not pick a random phase of the first jump.
            target.ai[0]=-160;
        }
        target.Bottom=new Vector2(x,floor*16);
        target.target=stationary?255:p.whoAmI;
        if(durable)target.life=target.lifeMax=mode=="agent-multi"?270:90; // Fixture only; the actual hits and cooldown remain native.
        var identity=new AiReadOnlyProtocol.Identity(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,p.whoAmI,null);
        File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(new{
            entityRef=NativeCombatWorld.Reference(identity,target),life=target.life,slot,mode,
            target.aiStyle,target.knockBackResist,cooldownAtSpawn=Cooldown(p,slot),spawnGameUpdate=Main.GameUpdateCount}));
    }
}
