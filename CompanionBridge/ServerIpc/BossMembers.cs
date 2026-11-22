using System;
using System.Collections.Generic;
using System.Linq;
using CompanionBridge.Common.Systems;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;

namespace CompanionBridge.ServerIpc;

// The seed identifies the encounter even when the original worm head is gone.
internal sealed class BossMembers(Identity binding,NPC seed)
{
    private sealed class Member(NPC npc,CombatTarget target,Guid token)
    {
        internal readonly NPC Npc=npc;
        internal readonly CombatTarget Target=target;
        internal readonly Guid Token=token;
        internal ulong? Death;
        internal int Previous,Next;
        internal bool Matches(NPC n)=>ReferenceEquals(Npc,n) && n.type==Target.TypeId && Id(n).Token==Token;
        internal bool Live=>Matches(Main.npc[Target.Slot]) && Npc.active;
    }
    private readonly Dictionary<int,Member> members=new();
    private IReadOnlyDictionary<int,Guid> permissions;
    private readonly int kind=seed.type;
    private readonly Guid seedToken=Id(seed).Token;
    private readonly int seedSlot=seed.whoAmI;
    internal CombatTarget Seed {get;}=new(seed.whoAmI,seed.type,NativeCombatWorld.Reference(binding,seed));
    internal ulong? Victory {get;private set;}
    internal object[] DeathProof {get;private set;}=Array.Empty<object>();
    internal int LiveCount {get;private set;}
    internal int PeakCount {get;private set;}
    internal int Heads {get;private set;}
    internal static AtomicNpcIdentity Id(NPC n)=>n.GetGlobalNPC<AtomicNpcIdentity>();
    internal static bool Supported(int type)=>type is NPCID.EyeofCthulhu or NPCID.EaterofWorldsHead or NPCID.TheDestroyer
        or NPCID.Retinazer or NPCID.Spazmatism or NPCID.SkeletronHead or NPCID.MoonLordCore || BossExtensions.Root(type);
    internal static object Discover(Identity frame,Player player)
    {
        var rows=new List<object>();
        foreach(var n in Main.ActiveNPCs)
        {
            if(n.life<=0 || Vector2.DistanceSquared(n.Center,player.Center)>640*640)continue;
            bool supported=Supported(n.type);
            if(!supported && !n.boss)continue;
            string reason=supported?new BossMembers(frame,n).Start():"boss_strategy_unknown";
            rows.Add(new{entityRef=NativeCombatWorld.Reference(frame,n),typeId=n.type,name=n.FullName,
                strategy=n.ModNPC!=null?"registered_root":supported?"vanilla":"observation_only",admissible=reason==null,reason});
        }
        return new{radiusPixels=640,candidates=rows,extensionConflicts=BossExtensions.Conflicts};
    }
    private bool Worm=>kind==NPCID.EaterofWorldsHead;
    private bool Twins=>kind is NPCID.Retinazer or NPCID.Spazmatism;
    private void Add(NPC n)
    {
        members[n.whoAmI]=new(n,new(n.whoAmI,n.type,NativeCombatWorld.Reference(binding,n)),Id(n).Token);
        permissions=null;
    }
    internal string Start()
    {
        Add(seed);
        if(Worm)
        {
            string error=Refresh();
            if(error!=null)return error;
            // An encounter started after an unobserved split cannot reconstruct
            // which detached branch belongs to it. Admit a complete seed chain.
            var visited=new HashSet<int>();
            var link=members[seedSlot];
            while(visited.Add(link.Target.Slot))
            {
                if(link.Target.TypeId==NPCID.EaterofWorldsTail && visited.Count==LiveCount)return null;
                int previous=link.Target.Slot;
                if(!members.TryGetValue(link.Next,out link) || link.Previous!=previous)break;
            }
            return "boss_worm_chain_unconfirmed";
        }
        if(!Twins)
        {
            var error=Refresh();
            if(kind==NPCID.TheDestroyer && (LiveCount<2 || seed.realLife!=seedSlot))return "boss_shared_life_unconfirmed";
            return error;
        }
        var eyes=Main.npc.Where(n=>n.active && n.type is NPCID.Retinazer or NPCID.Spazmatism).ToArray();
        if(eyes.Length!=2 || eyes.All(n=>n.type==kind) || eyes.Any(n=>Id(n).AmbiguousTwinSpawn))
            return "boss_twins_pair_ambiguous";
        var partner=eyes.Single(n=>n!=seed);
        if(Id(partner).SpawnUpdate!=Id(seed).SpawnUpdate || Id(partner).TwinOrigin!=Id(seed).TwinOrigin)
            return "boss_twins_pair_unconfirmed";
        Add(partner);
        return Refresh();
    }
    internal bool Contains(NPC n)=>members.TryGetValue(n.whoAmI,out var m)
        && ReferenceEquals(Main.npc[n.whoAmI],n) && m.Matches(n);
    internal CombatTarget Ref(NPC n)=>members[n.whoAmI].Target;
    internal IReadOnlyDictionary<int,Guid> Permissions()=>permissions??=members.Where(x=>x.Value.Live).ToDictionary(x=>x.Key,x=>x.Value.Token);
    internal bool Attackable(NPC n)=>Contains(n) && n.active && n.life>0 && !n.friendly && !n.dontTakeDamage
        && (n==seed || Worm || Twins || Attached(n)) && n.CanBeChasedBy(binding.Actor);
    internal NPC Carrier(NPC n)=>kind==NPCID.TheDestroyer || n!=seed && n.realLife==seedSlot && Attached(n)?seed:n;
    internal void Death(NPC n)
    {
        if(Contains(n) && n.life<=0){members[n.whoAmI].Death=Main.GameUpdateCount;permissions=null;}
    }
    private bool Attached(NPC n)
    {
        if(Id(n).Family!=seedToken)return false;
        return kind switch
        {
            NPCID.TheDestroyer => n.type is NPCID.TheDestroyerBody or NPCID.TheDestroyerTail && n.realLife==seedSlot && Contains(seed),
            NPCID.SkeletronHead => n.type==NPCID.SkeletronHand && n.ai[1]==seedSlot && Contains(seed),
            NPCID.MoonLordCore => n.type is NPCID.MoonLordHead or NPCID.MoonLordHand && n.ai[3]==seedSlot && Contains(seed),
            NPCID.EaterofWorldsHead => n.type is NPCID.EaterofWorldsBody or NPCID.EaterofWorldsTail,
            _ => seed.ModNPC!=null && Contains(seed) && (n.realLife==seedSlot || n.realLife<0 && BossExtensions.Link(seed,n))
        };
    }
    // SetDefaults replaces the instance token on a split. Require the previous
    // neighbour's observed death AND the surviving reciprocal edge.
    private bool Split(Member old,NPC n)
    {
        if(old.Target.TypeId!=NPCID.EaterofWorldsBody || !n.active || Id(n).Family!=Guid.Empty)return false;
        bool head=n.type==NPCID.EaterofWorldsHead;
        if(!head && n.type!=NPCID.EaterofWorldsTail)return false;
        int cut=head?old.Previous:old.Next, intact=head?old.Next:old.Previous;
        return members.TryGetValue(cut,out var lost) && lost.Death.HasValue
            && members.TryGetValue(intact,out var neighbor) && neighbor.Live
            && n.ai[head?0:1]==intact && neighbor.Npc.ai[head?1:0]==n.whoAmI;
    }
    internal string Refresh()
    {
        if(Worm)
        {
            foreach(var old in members.Values.ToArray())
            {
                if(old.Death.HasValue || old.Live)continue;
                NPC replacement=Main.npc[old.Target.Slot];
                if(!Split(old,replacement))return "boss_worm_lineage_unconfirmed";
                Add(replacement);
            }
        }
        if(seed.ModNPC!=null || kind is NPCID.TheDestroyer or NPCID.SkeletronHead or NPCID.MoonLordCore or NPCID.EaterofWorldsHead)
            foreach(var n in Main.ActiveNPCs)
                if(!Contains(n) && Attached(n))Add(n);
        LiveCount=Heads=0;
        foreach(var m in members.Values)
        {
            if(!m.Live)continue;
            LiveCount++;
            if(m.Npc.type==NPCID.EaterofWorldsHead)Heads++;
            if(Worm){m.Next=(int)m.Npc.ai[0];m.Previous=(int)m.Npc.ai[1];}
        }
        PeakCount=Math.Max(PeakCount,LiveCount);
        var required=Worm || Twins?members.Values.AsEnumerable():new[]{members[seedSlot]};
        if(required.Any(m=>!m.Live && !m.Death.HasValue))return "boss_member_lost";
        if(required.All(m=>!m.Live && m.Death.HasValue))
        {
            Victory=required.Max(m=>m.Death);
            DeathProof=required.Select(m=>(object)new{member=m.Target,update=m.Death.Value}).ToArray();
        }
        return null;
    }
    internal NPC Select(Player p,NPC previous)
    {
        NPC best=null;
        float bestScore=float.MaxValue;
        foreach(var m in members.Values)
        {
            var n=m.Npc;
            if(!m.Live || !Attackable(n))continue;
            float score=Vector2.Distance(p.Center,n.Center);
            if(n==previous)score-=32;
            if(n.type==NPCID.SkeletronHand || n.type==NPCID.MoonLordCore)score-=640;
            if(score<bestScore){best=n;bestScore=score;}
        }
        return best;
    }
}
