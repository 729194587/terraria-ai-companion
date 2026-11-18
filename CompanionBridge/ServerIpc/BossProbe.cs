using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CompanionBridge.Common.Systems;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace CompanionBridge.ServerIpc;

// Read-only observer. Only loaded by the isolated diagnostic launcher.
public sealed class BossProbe : GlobalNPC
{
    public override bool IsLoadingEnabled(Mod mod) => Main.dedServ && Program.LaunchParameters.ContainsKey("-companionbossprobe")
        && ServerAiActionFixture.Enabled;
    internal static IEnumerable<NPC> Members => Main.npc.Take(Main.maxNPCs).Where(n=>n.active && Relevant(n.type));
    private static bool Relevant(int type) => type is >=13 and <=15 or 125 or 126 or >=134 and <=136 or >=396 and <=400;
    internal static object Row(NPC n) => new {slot=n.whoAmI,n.type,token=n.GetGlobalNPC<AtomicNpcIdentity>().Token,
        n.life,n.lifeMax,n.active,n.realLife,n.boss,n.dontTakeDamage,ai=n.ai.ToArray()};
    private static readonly List<object> events=new();
    private static readonly HashSet<int> sampledProjectileTypes=new();
    internal static void Event(string kind,object data)=>events.Add(new{tick=Main.GameUpdateCount,kind,data});
    internal static void SampleThreats()
    {
        if(sampledProjectileTypes.Count>=8)return;
        foreach(var p in Main.projectile)
        {
            if(!p.active || !p.hostile || !sampledProjectileTypes.Add(p.type))continue;
            Event("hostileProjectile",new{slot=p.whoAmI,p.type,p.owner,p.identity,p.damage,p.width,p.height,
                x=p.Center.X,y=p.Center.Y,vx=p.velocity.X,vy=p.velocity.Y});
            if(sampledProjectileTypes.Count>=8)break;
        }
    }
    internal static void Snapshot(string name)=>Event(name,Members.Select(Row).ToArray());
    internal static void WorldFlags(string name)=>Event(name,new{NPC.downedBoss2,NPC.downedMechBoss1,NPC.downedMechBoss2,NPC.downedMoonlord});
    public override void OnSpawn(NPC npc,IEntitySource source)
    {
        if(Relevant(npc.type))Event("spawn",new{npc=Row(npc),source=source.GetType().Name,
            parent=source is EntitySource_Parent {Entity:NPC p}?Row(p):null});
    }
    public override bool CheckDead(NPC npc){if(Relevant(npc.type))Event("checkDead",Row(npc));return true;}
    public override void OnKill(NPC npc){if(Relevant(npc.type))Event("onKill",Row(npc));}
    internal static void Finish(string scenario)
    {
        string path=Path.Combine(Main.SavePath,"boss-e0.json");
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(new{scenario,engine=typeof(Main).Assembly.GetName().Version.ToString(),events}));
        File.Move(path+".tmp",path);
    }
    public override void Unload(){events.Clear();sampledProjectileTypes.Clear();}
}

// Stimuli for the copied world only. Never writes NPC parent links, life, death flags or AI state.
internal static class BossProbeScenario
{
    private static int frame;
    private static bool done;
    private static void Spawn(Player actor,int type,int offset)
    {
        int slot=NPC.NewNPC(actor.GetSource_Misc("BossProbeScenario"),(int)actor.Center.X+offset,(int)actor.Center.Y,type);
        if(slot>=Main.maxNPCs)throw new InvalidOperationException("Boss probe spawn failed");
        BossProbe.Event("seed",BossProbe.Row(Main.npc[slot]));
    }
    private static void Strike(NPC n,int amount)
    {
        NPC root=n.realLife>=0 && n.realLife<Main.maxNPCs?Main.npc[n.realLife]:null;
        object before=BossProbe.Row(n),rootBefore=root==null?null:BossProbe.Row(root);
        int damage=n.StrikeNPC(new NPC.HitInfo{Damage=amount});
        BossProbe.Event("strike",new{before,after=BossProbe.Row(n),rootBefore,rootAfter=root==null?null:BossProbe.Row(root),damage});
    }
    internal static void Advance(Player actor,string scenario)
    {
        if(done)return;
        frame++;
        // Hold the test actor alive and available to the native boss AI.
        actor.immune=true;actor.immuneTime=60;
        if(frame==1)
        {
            Main.dayTime=false;Main.time=1000;
            switch(scenario)
            {
                case "eater": Spawn(actor,NPCID.EaterofWorldsHead,240);break;
                case "destroyers": Spawn(actor,NPCID.TheDestroyer,-240);Spawn(actor,NPCID.TheDestroyer,240);break;
                case "twins": Spawn(actor,NPCID.Retinazer,-240);Spawn(actor,NPCID.Spazmatism,240);break;
                case "two-twins":
                    // Interleave two fixture-labelled pairs sharing a player and spawn frame.
                    Spawn(actor,NPCID.Retinazer,-240);Spawn(actor,NPCID.Retinazer,240);
                    Spawn(actor,NPCID.Spazmatism,240);Spawn(actor,NPCID.Spazmatism,-240);
                    break;
                case "moon": Spawn(actor,NPCID.MoonLordCore,240);break;
                default: throw new ArgumentException("Unknown boss probe case");
            }
        }
        var members=BossProbe.Members.ToArray();
        BossProbe.SampleThreats();
        if(frame==12){BossProbe.Snapshot("before");BossProbe.WorldFlags("flagsBefore");}
        if(frame==12 && scenario=="eater")
        {
            var bodies=members.Where(n=>n.type==NPCID.EaterofWorldsBody).ToArray();
            Strike(bodies[bodies.Length/2],999999);
        }
        if(frame==12 && scenario=="destroyers")
            Strike(members.First(n=>n.type==NPCID.TheDestroyerBody),123);
        if(scenario=="twins" && (frame==12 || frame==24))
            Strike(members.Single(n=>n.type==(frame==12?NPCID.Retinazer:NPCID.Spazmatism)),999999);
        if(frame==18){BossProbe.Snapshot("after");BossProbe.WorldFlags("flagsAfter");}
        if(scenario=="moon" && frame%8==0)
        {
            foreach(var n in members.Where(n=>n.type is NPCID.MoonLordHead or NPCID.MoonLordHand or NPCID.MoonLordCore))
                if(!n.dontTakeDamage && n.life>1)Strike(n,999999);
        }
        bool finished=scenario switch
        {
            "moon" => frame>30 && members.All(n=>n.type!=NPCID.MoonLordCore) || frame>=3200,
            "two-twins" => frame==420,
            _ => frame==32
        };
        if(finished)
        {
            BossProbe.Snapshot("end");
            BossProbe.WorldFlags("flagsEnd");
            BossProbe.Finish(scenario);
            done=true;
        }
    }
}
