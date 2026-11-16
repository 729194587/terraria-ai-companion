using System;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Only the isolated, copied-world harness can seed a target. All attacks run in natural frames.
internal static class ServerCombatFixture
{
    internal static bool Enabled => ServerAiActionFixture.Enabled && Program.LaunchParameters.ContainsKey("-companioncombattest");
    private static NPC target;
    private static ulong? lastFrame,lastHit;
    private static int frames,hits,hitViolations;
    private static bool faultNextPress;

    internal static void Install(Action<Action,Action> install)
    {
        if(Program.LaunchParameters.ContainsKey("-companioncombattest"))
            install(()=>On_Player.ItemCheck+=ObserveItemFrame,()=>On_Player.ItemCheck-=ObserveItemFrame);
    }

    private static void ObserveItemFrame(On_Player.orig_ItemCheck orig,Player p)
    {
        if(!Enabled||!NativeCombatWorld.AllowsItem(p)){orig(p);return;}
        ulong tick=Main.GameUpdateCount;
        if(lastFrame==tick||!NativeItemInput.Active(p)||Main.myPlayer!=p.whoAmI||Main.netMode!=NetmodeID.Server)
            throw new InvalidOperationException("Combat fixture observed duplicate or unscoped native ItemCheck.");
        lastFrame=tick;frames++;
        if(faultNextPress&&p.controlUseItem)
        {
            faultNextPress=false;
            // tML catches ModPlayer callback faults; inject at the native
            // boundary so this diagnostic exercises the wrapper unwind.
            throw new InvalidOperationException("Intentional native combat input fault.");
        }
        orig(p);
    }

    internal static void ObserveHit(Player p,NPC npc)
    {
        if(!Enabled||!NativeCombatWorld.AllowsItem(p))return;
        ulong tick=Main.GameUpdateCount;
        if(lastHit==tick||lastFrame!=tick||!ReferenceEquals(npc,target)||!NativeItemInput.Active(p))
            hitViolations++;
        lastHit=tick;hits++;
    }
    internal static bool Keeps(NPC npc) => Enabled && ReferenceEquals(npc,target);
    internal static void RecordDeath(NPC npc)
    {
        if(!Keeps(npc))return;
        File.AppendAllText(Path.Combine(Main.SavePath,"combat-deaths.jsonl"),
            JsonSerializer.Serialize(new{gameUpdate=Main.GameUpdateCount,npc.whoAmI,npc.life,
                stack=new System.Diagnostics.StackTrace().ToString()})+Environment.NewLine);
    }

    internal static void Tick(Player p)
    {
        if(!Enabled)return;
        // The copied world can contain town NPCs whose attacks would decide
        // the fight. Keep the arena isolated from ambient friendly projectiles.
        foreach(var projectile in Main.projectile)
            if(projectile.active&&projectile.friendly)projectile.active=false;
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
                JsonSerializer.Serialize(new{mode,frames,hits,hitViolations,inputScopeReleased=!NativeItemInput.Active(p)&&Main.myPlayer!=p.whoAmI,
                    attackReleased=!p.controlUseItem&&p.releaseUseItem}));
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
        lastFrame=lastHit=null;frames=hits=hitViolations=0;faultNextPress=false;
        if(mode=="agent"&&p.HeldItem.type==ItemID.WoodenSword)
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
        int x=(int)p.Center.X+(mode=="agent"?-180:-24);
        // Keep the reflex spawn on a different slot from the preceding kill,
        // whose native per-slot melee cooldown can still be active.
        int slot=NPC.NewNPC(p.GetSource_Misc("isolated_combat_fixture"),x,floor*16,NPCID.GreenSlime,
            Start:mode=="reflex"?10:0,
            Target:mode=="agent"?255:p.whoAmI);
        target=Main.npc[slot];
        if(mode=="agent")
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
        target.target=mode=="agent"?255:p.whoAmI;
        var identity=new AiReadOnlyProtocol.Identity(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,p.whoAmI,null);
        File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(new{
            entityRef=NativeCombatWorld.Reference(identity,target),life=target.life,slot,mode,
            target.aiStyle,target.knockBackResist,spawnGameUpdate=Main.GameUpdateCount}));
    }
}
