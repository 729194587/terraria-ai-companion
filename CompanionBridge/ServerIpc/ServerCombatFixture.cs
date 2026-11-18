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
    private static int presses,spawnPackets,ammoPackets,manaPackets;
    // Per-frame native mana ledger: every ItemCheck's start/end mana and
    // whether that frame emitted a verified shot, so a debit without a
    // projectile can never hide behind end-of-run totals.
    private static readonly List<object> manaTrace=new();
    private static int manaPaidWithoutShot, verifiedShots, potionPackets, summonSyncPackets;
    private static int? firstCooldown;
    private static readonly List<object> hitTrace=new();
    private static readonly List<object> shots=new(),projectileHits=new(),flightTrace=new();
    private static readonly List<object> summonDamage=new();
    private static bool parkIce;
    private static NativeCombatWorld.Shot parked;

    internal static void ObserveSummonHit(Projectile projectile,NPC npc,int damage,NativeCombatWorld.Shot shot,bool credited)
    {
        if(!Enabled)return;
        summonDamage.Add(new{tick=Main.GameUpdateCount,projectile.type,shot.Launched,shot.Identity,damage,npc.life,credited,
            target=NativeCombatWorld.Reference(Bound(ProbeSession.Actor),npc)});
    }

    // Hold the issued ice bolt at the first update across a task handoff. Its
    // spawn, trajectory, collision and damage all still use the native paths.
    private static void HoldIce(On_Projectile.orig_Update orig,Projectile p,int index)
    {
        if(parkIce && p.active && p.TryGetGlobalProjectile<CombatProjectile>(out var trace)
            && trace.Flight is {Verified:true, ParentIdentity:not null, Combat:not null} bolt)
        {
            parked ??= bolt;
            if(ReferenceEquals(parked,bolt))return;
        }
        orig(p,index);
    }

    internal static void ObserveProjectileSpawn(Projectile projectile,NativeCombatWorld.Shot shot)
    {
        if(!Enabled)return;
        var stack=new System.Diagnostics.StackTrace().ToString();
        if (shot.Verified) verifiedShots++;
        var aim=shot.Target==null ? null : Main.npc[shot.Target.Slot];
        shots.Add(new{tick=Main.GameUpdateCount,shot.Owner,shot.Identity,shot.Weapon,shot.Ammo,shot.Magic,shot.Verified,
            shot.Type,shot.Slot,shot.PersistentRoot,x=projectile.Center.X,y=projectile.Center.Y,tx=aim?.Center.X,ty=aim?.Center.Y,
            targetVx=aim?.velocity.X,targetVy=aim?.velocity.Y,target=shot.Target?.EntityRef,vx=projectile.velocity.X,vy=projectile.velocity.Y,
            mana=shot.Combat?.ManaAtLaunch,damage=projectile.damage,
            nativeShoot=stack.Contains("ItemCheck_Shoot"),nativeWrapper=stack.Contains("Terraria.Player::ItemCheckWrapped")});
    }
    internal static void ObserveProjectileHit(Projectile p,NPC n,int damage,NativeCombatWorld.Shot shot)
    {
        if(!Enabled)return;
        var stack=new System.Diagnostics.StackTrace().ToString();
        projectileHits.Add(new{tick=Main.GameUpdateCount,p.identity,p.owner,p.type,n.life,damage,launched=shot.Launched,
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
        install(()=>On_Projectile.Update+=HoldIce,()=>On_Projectile.Update-=HoldIce);
        install(()=>On_Player.ItemCheck+=ObserveItemFrame,()=>On_Player.ItemCheck-=ObserveItemFrame);
        install(()=>On_NetMessage.SendData+=ObservePackets,()=>On_NetMessage.SendData-=ObservePackets);
    }

    private static void ObservePackets(On_NetMessage.orig_SendData orig,int msg,int remote,int ignore,NetworkText text,
        int number,float a,float b,float c,int d,int e,int f)
    {
        orig(msg,remote,ignore,text,number,a,b,c,d,e,f);
        var p=ProbeSession.Actor;
        if(p==null||!Enabled)return;
        if(msg==MessageID.SyncProjectile && Main.projectile[number].TryGetGlobalProjectile<CombatProjectile>(out var summon)
            && summon.Flight is {Verified:true} flight && (flight.PersistentRoot || flight.ParentIdentity.HasValue)
            && flight.Owns(Main.projectile[number])) summonSyncPackets++;
        if(!NativeCombatWorld.AllowsItem(p))return;
        if(msg==MessageID.SyncProjectile&&NativeItemInput.Active(p)
            &&Main.projectile[number].TryGetGlobalProjectile<CombatProjectile>(out var trace)&&trace.Flight is {Verified:true})
            spawnPackets++;
        if(msg==MessageID.SyncEquipment&&number==p.whoAmI&&a==54)ammoPackets++;
        if(msg==MessageID.SyncEquipment&&number==p.whoAmI&&a==50)potionPackets++;
        if(msg==MessageID.PlayerMana&&number==p.whoAmI)manaPackets++;
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
        int manaBefore=p.statMana;
        int shotsBefore=verifiedShots;
        int potionsBefore=FixturePotions(p);
        orig(p);
        int manaAfter=p.statMana;
        int launched=verifiedShots-shotsBefore;
        int potionsUsed=potionsBefore-FixturePotions(p);
        // Whole-frame mana ledger: payment, QuickMana recovery and the shot all
        // happen inside this one native ItemCheck, so a debit without a
        // projectile is recorded here even when later payment offsets it.
        if(manaBefore!=manaAfter||potionsUsed>0)
        {
            manaTrace.Add(new{tick,manaBefore,manaAfter,debit=Math.Max(0,manaBefore-manaAfter),shots=launched,potionsUsed,
                pressed=p.controlUseItem,flower=p.manaFlower,potionDelay=p.potionDelay,
                manaCost=p.GetManaCost(p.HeldItem)});
            if((manaAfter<manaBefore||potionsUsed>0)&&launched==0)manaPaidWithoutShot++;
        }
    }

    private static int FixturePotions(Player p) => p.inventory[50].type==ItemID.ManaPotion ? p.inventory[50].stack : 0;

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

    // ResetEffects rebuilds statManaMax2 from statManaMax every frame, so the
    // fixture must seed the base pool rather than only the derived maximum.
    private static void ManaPool(Player p,int max,int value)
    {
        p.statManaMax=p.statManaMax2=max;
        p.ConsumedManaCrystals=Math.Clamp((max-20)/20,0,9);
        p.statMana=Math.Clamp(value,0,max);
        p.manaFlower=false;
    }

    // The eight admitted magic weapons. Modes map to fixture setup names so the
    // server test can prove each one emits its own native bolt.
    private static readonly (string Mode,int Type)[] staffs=
    {
        ("staff-spark",ItemID.WandofSparking),("staff-amethyst",ItemID.AmethystStaff),
        ("staff-topaz",ItemID.TopazStaff),("staff-sapphire",ItemID.SapphireStaff),
        ("staff-emerald",ItemID.EmeraldStaff),("staff-ruby",ItemID.RubyStaff),
        ("staff-diamond",ItemID.DiamondStaff),("staff-amber",ItemID.AmberStaff),
    };
    private static int? Staff(string mode)
    {
        foreach(var (name,type) in staffs)if(name==mode)return type;
        return null;
    }
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

    // Spell fire debuffs tick damage between casts, which would let a DOT
    // finish the target without a projectile and confuse the payment/launch
    // ledger. Magic fixtures keep kills attributable to the verified bolt.
    private static void IgnoreSpellDots(NPC n)
    {
        n.buffImmune[BuffID.OnFire]=true;
        n.buffImmune[BuffID.OnFire3]=true;
        n.buffImmune[BuffID.Frostburn]=true;
        n.buffImmune[BuffID.Frostburn2]=true;
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
                JsonSerializer.Serialize(new{mode,gameUpdate=Main.GameUpdateCount,frames,hits,hitViolations,firstCooldown,hitTrace,shots,projectileHits,flightTrace,spawnPackets,ammoPackets,manaPackets,mana=p.statMana,
                    ammo=p.inventory[54].stack,spareAmmo=p.inventory[53].stack,
                    survivors=defenseTargets.Where(n=>n.active).Select(n=>DefenseIdentity(p,n)),inputScopeReleased=!NativeItemInput.Active(p)&&Main.myPlayer!=p.whoAmI,
                    attackReleased=!p.controlUseItem&&p.releaseUseItem,
                    p.dead,generation=ProbeSession.ActorGeneration,summonSyncPackets,focus=p.MinionAttackTargetNPC,babySlime=p.HasBuff(BuffID.BabySlime),p.maxMinions,p.maxTurrets,
                    summons=Main.projectile.Where(q=>q.active && q.TryGetGlobalProjectile<CombatProjectile>(out var trace)
                        && trace.Flight is {PersistentRoot:true}).Select(q=>new {q.type,q.identity,q.whoAmI,q.owner,q.minionSlots,q.sentry,authorized=q.GetGlobalProjectile<CombatProjectile>().Flight.Owns(q)}),
                    summonDamage,parkedIce=parked?.Launched,manaTrace=manaTrace.ToArray(),manaPaidWithoutShot,potionPackets,
                    potions=FixturePotions(p),potionDelay=p.potionDelay,manaFlower=p.manaFlower}));
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
                &&trace.Flight?.Target?.Slot==oldSlot);
            old.active=false;
            target=SpawnDefense(p,-300,32,threatening:false,start:oldSlot);
            target.Bottom=bottom;
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(new{
                slot=target.whoAmI,entityRef=NativeCombatWorld.Reference(Bound(p),target),target.life,live}));
            return;
        }
        if(mode is "magic-replace" or "magic-cancel")
        {
            var old=target;
            int oldSlot=old.whoAmI;
            var bottom=old.Bottom;
            bool live=Main.projectile.Any(projectile=>projectile.active&&projectile.TryGetGlobalProjectile<CombatProjectile>(out var trace)
                &&trace.Flight?.Target?.Slot==oldSlot&&trace.Flight.Magic);
            if(mode=="magic-replace")
            {
                old.active=false;
                target=SpawnDefense(p,-300,32,threatening:false,start:oldSlot);
                target.Bottom=bottom;
                File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(new{
                    slot=target.whoAmI,entityRef=NativeCombatWorld.Reference(Bound(p),target),target.life,live}));
            }
            else File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(new{live}));
            return;
        }
        if(mode=="release-ice")
        {
            parkIce=false;
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),"{}");
            return;
        }
        if(mode=="autonomous-target")
        {
            if(target!=null)target.active=false;
            target=SpawnDefense(p,-165,1800,threatening:false);
            IgnoreSpellDots(target);
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                JsonSerializer.Serialize(DefenseIdentity(p,target)));
            return;
        }
        if(mode=="clear-fixture-summons")
        {
            p.ClearBuff(BuffID.BabySlime);
            foreach(var q in Main.ActiveProjectiles)
                if(q.TryGetGlobalProjectile<CombatProjectile>(out var trace) && trace.Flight is {SummonAttack:true})
                    q.Kill();
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),"{}");
            return;
        }
        if(mode=="summon-defense-join")
        {
            var added=SpawnDefense(p,85,60);
            var bystander=SpawnDefense(p,-180,60,threatening:false);
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(new{
                added=DefenseIdentity(p,added),bystander=DefenseIdentity(p,bystander)}));
            return;
        }
        if(mode is "magic-defense" or "magic-defense-join")
        {
            p.immune=true;p.immuneTime=600;
            p.HeldItem.SetDefaults(ItemID.WandofSparking);
            ManaPool(p,100,80);
            if(mode=="magic-defense")
            {
                // Starting a new defence retires any leftover fixture NPC; the
                // join step must keep the encounter it is extending.
                foreach(var n in defenseTargets)n.active=false;
                defenseTargets.Clear();
                target=SpawnDefense(p,-26,150);
                IgnoreSpellDots(target);
                File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                    JsonSerializer.Serialize(new{first=DefenseIdentity(p,target)}));
            }
            else
            {
                var added=SpawnDefense(p,90,50);
                var bystander=SpawnDefense(p,-160,50,threatening:false);
                IgnoreSpellDots(added);IgnoreSpellDots(bystander);
                File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                    JsonSerializer.Serialize(new{added=DefenseIdentity(p,added),bystander=DefenseIdentity(p,bystander)}));
            }
            return;
        }
        if(mode is "summon-death" or "summon-generation")
        {
            long before=ProbeSession.ActorGeneration;
            if(mode=="summon-generation") ProbeSession.ActorGeneration++;
            else NativeSurvival.Run(p,()=>p.KillMe(Terraria.DataStructures.PlayerDeathReason.ByCustomReason(
                NetworkText.FromLiteral("Isolated summon lifecycle test")),9999,0));
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(new{
                before,generation=ProbeSession.ActorGeneration,p.dead,target.life}));
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
        bool durable=mode is "agent-multi" or "durable" or "reuse" or "magic-multi";
        bool distant=mode is "agent" or "agent-multi" or "magic-multi";
        bool stationary=durable||distant;
        int startSlot=mode=="reflex"?10:0;
        if(mode=="reuse")
        {
            if(target==null||target.active||target.life>0)
                throw new InvalidOperationException("Reuse requires the preceding defeated NPC.");
            startSlot=target.whoAmI;
        }
        lastFrame=lastHit=null;frames=hits=hitViolations=presses=spawnPackets=ammoPackets=manaPackets=0;faultNextPress=false;
        manaPaidWithoutShot=verifiedShots=potionPackets=summonSyncPackets=0;
        parkIce=mode=="summon-inflight";parked=null;summonDamage.Clear();
        firstCooldown=null;hitTrace.Clear();shots.Clear();projectileHits.Clear();flightTrace.Clear();manaTrace.Clear();
        // Accessory effects are rebuilt each frame, so clear the fixture slot
        // before a mode opts into Mana Flower.
        p.armor[3].TurnToAir();
        if(distant&&(p.HeldItem.type is ItemID.WoodenSword or ItemID.StaffoftheFrostHydra||CombatBallistics.Supported(p.HeldItem)))
        {
            p.HeldItem.SetDefaults(ItemID.PlatinumBroadsword);
            p.statLife=p.statLifeMax2;
        }
        if(mode=="magic-multi")
        {
            p.HeldItem.SetDefaults(ItemID.AmethystStaff);
            ManaPool(p,400,400);
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
        if(mode.StartsWith("summon-"))
        {
            bool slime=mode.Contains("slime");
            p.HeldItem.SetDefaults(slime ? ItemID.SlimeStaff : ItemID.StaffoftheFrostHydra);
            ManaPool(p,100,mode.EndsWith("reuse") ? 0 : 100);
            if(mode=="summon-defense")
            {
                p.immune=true;p.immuneTime=600;
                target=SpawnDefense(p,-75,320);
                File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                    JsonSerializer.Serialize(new{first=DefenseIdentity(p,target)}));
                return;
            }
            target=SpawnDefense(p,-145,mode.EndsWith("cancel") ? 1800 : parkIce ? 8 : slime ? 60 : 240,threatening:false);
            IgnoreSpellDots(target);
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(new {
                entityRef=NativeCombatWorld.Reference(Bound(p),target),target.whoAmI,p.HeldItem.shoot,
                manaCost=p.GetManaCost(p.HeldItem),mana=p.statMana}));
            return;
        }
        if(mode is "bow" or "gun" or "bow-reflex" or "bow-empty" or "bow-one" or "bow-wall" or "bow-high" or "bow-sealed" or "bow-opposite" or "bow-no-route")
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
            if(mode is "bow-opposite" or "bow-no-route")
            {
                int tx=(int)(target.Center.X/16);
                target.Center=new Vector2(tx*16+8,target.Center.Y);
                // Two-tile-high shelter: projectiles can enter only from the
                // left, and the player cannot stand inside it.
                for(int dx=-2;dx<=2;dx++)Wall(tx+dx,floor-3);
                for(int dy=1;dy<=2;dy++)Wall(tx+2,floor-dy);
                if(mode=="bow-no-route")
                {
                    // Leave only a few viable shooting stances between the
                    // shelter mouth and a high wall; trap the player separately.
                    for(int dy=1;dy<=12;dy++)Wall(tx-5,floor-dy);
                    int px=(int)(p.Center.X/16);
                    for(int dx=-2;dx<=2;dx++)
                    for(int dy=1;dy<=4;dy++)
                        if(Math.Abs(dx)==2||dy==4)Wall(px+dx,floor-dy);
                }
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
        if(mode is "spark" or "amethyst" or "no-mana" or "magic-reflex")
        {
            bool reflex=mode=="magic-reflex",noMana=mode=="no-mana",amethyst=mode=="amethyst";
            p.HeldItem.SetDefaults(amethyst?ItemID.AmethystStaff:ItemID.WandofSparking);
            p.inventory[54].TurnToAir();p.inventory[53].TurnToAir();
            ManaPool(p,100,noMana?0:80);
            p.manaCost=1f;
            p.manaFlower=false;
            target=SpawnDefense(p,reflex?-95:-170,amethyst?55:50,threatening:reflex);
            IgnoreSpellDots(target);
            target.noGravity=true;
            if(amethyst)target.velocity=new Vector2(-.4f,-.12f);
            bool initialAim=CombatBallistics.ReadRound(p,p.HeldItem,out var round)==null
                &&CombatBallistics.Aim(p,target,p.HeldItem,round,out _);
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                JsonSerializer.Serialize(new{slot=target.whoAmI,entityRef=NativeCombatWorld.Reference(Bound(p),target),
                    weapon=p.HeldItem.type,weaponName=p.HeldItem.Name,shoot=p.HeldItem.shoot,mana=p.statMana,manaMax=p.statManaMax2,
                    manaCost=p.GetManaCost(p.HeldItem),initialAim,startX=p.Center.X,startY=p.Center.Y}));
            return;
        }
        if(mode=="mana-exact")
        {
            // Mana exactly equals the native cast cost: ItemCheck_PayMana
            // empties the pool, so any post-payment re-check would drop the shot.
            p.HeldItem.SetDefaults(ItemID.WandofSparking);
            p.inventory[54].TurnToAir();p.inventory[53].TurnToAir();
            ManaPool(p,400,0);
            p.manaCost=1f;
            p.manaFlower=false;
            int cost=p.GetManaCost(p.HeldItem);
            p.statMana=cost;
            // One native cast must finish the target, so the exact-cost debit
            // is the only payment in the run and later regeneration cannot
            // fund a second, ambiguous shot.
            target=SpawnDefense(p,-170,10,threatening:false);
            IgnoreSpellDots(target);
            target.noGravity=true;
            target.defense=0;target.lifeMax=10;
            bool initialAim=CombatBallistics.ReadRound(p,p.HeldItem,out var round)==null
                &&CombatBallistics.Aim(p,target,p.HeldItem,round,out _);
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                JsonSerializer.Serialize(new{slot=target.whoAmI,entityRef=NativeCombatWorld.Reference(Bound(p),target),
                    weapon=p.HeldItem.type,shoot=p.HeldItem.shoot,mana=p.statMana,manaMax=p.statManaMax2,
                    manaCost=cost,initialAim,startX=p.Center.X,startY=p.Center.Y}));
            return;
        }
        if(mode is "mana-flower" or "mana-flower-dry")
        {
            // No mana for the cast; the native Mana Flower QuickMana path owns
            // both potion use and payment. The AI must not pre-refuse it.
            bool dry=mode=="mana-flower-dry";
            p.HeldItem.SetDefaults(ItemID.WandofSparking);
            p.inventory[54].TurnToAir();p.inventory[53].TurnToAir();
            ManaPool(p,400,0);
            p.manaCost=1f;
            // manaFlower is rebuilt from accessories by ResetEffects each frame;
            // equip the real Mana Flower so native QuickMana stays enabled.
            p.armor[3].SetDefaults(ItemID.ManaFlower);p.armor[3].stack=1;
            p.potionDelay=0;
            if(dry)p.inventory[50].TurnToAir();
            else
            {
                p.inventory[50].SetDefaults(ItemID.ManaPotion);
                p.inventory[50].stack=3;
            }
            // One cast must finish the target: the run then holds exactly one
            // QuickMana potion payment instead of a chain of them.
            target=SpawnDefense(p,-170,10,threatening:false);
            IgnoreSpellDots(target);
            target.noGravity=true;
            int cost=p.GetManaCost(p.HeldItem);
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                JsonSerializer.Serialize(new{slot=target.whoAmI,entityRef=NativeCombatWorld.Reference(Bound(p),target),
                    weapon=p.HeldItem.type,mana=p.statMana,manaMax=p.statManaMax2,manaCost=cost,potionDelay=p.potionDelay,
                    potions=FixturePotions(p),flower=p.manaFlower}));
            return;
        }
        if(Staff(mode) is {} staffType)
        {
            p.HeldItem.SetDefaults(staffType);
            p.inventory[54].TurnToAir();p.inventory[53].TurnToAir();
            ManaPool(p,400,400);
            p.manaCost=1f;
            p.manaFlower=false;
            target=SpawnDefense(p,-170,60,threatening:false);
            IgnoreSpellDots(target);
            target.noGravity=true;
            bool initialAim=CombatBallistics.ReadRound(p,p.HeldItem,out var round)==null
                &&CombatBallistics.Aim(p,target,p.HeldItem,round,out _);
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),
                JsonSerializer.Serialize(new{slot=target.whoAmI,entityRef=NativeCombatWorld.Reference(Bound(p),target),
                    weapon=p.HeldItem.type,weaponName=p.HeldItem.Name,shoot=p.HeldItem.shoot,
                    mana=p.statMana,manaCost=p.GetManaCost(p.HeldItem),initialAim,startX=p.Center.X,startY=p.Center.Y}));
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
        if(durable)target.life=target.lifeMax=mode is "agent-multi" or "magic-multi"?270:90; // Fixture only; the actual hits and cooldown remain native.
        var identity=new AiReadOnlyProtocol.Identity(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,p.whoAmI,null);
        File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(new{
            entityRef=NativeCombatWorld.Reference(identity,target),life=target.life,slot,mode,
            weapon=p.HeldItem.type,mana=p.statMana,manaMax=p.statManaMax2,manaCost=p.GetManaCost(p.HeldItem),
            target.aiStyle,target.knockBackResist,cooldownAtSpawn=Cooldown(p,slot),spawnGameUpdate=Main.GameUpdateCount}));
    }
}
