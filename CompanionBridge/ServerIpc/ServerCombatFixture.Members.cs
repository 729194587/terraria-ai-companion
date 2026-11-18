using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Microsoft.Xna.Framework;

namespace CompanionBridge.ServerIpc;

internal static partial class ServerCombatFixture
{
    private static string memberCase;
    private static NPC foreignRoot,farSegment,nearSegment,replacement;
    private static bool crossedReplacement;
    private static void AdvanceMemberFixture(Player p)
    {
        p.immune=true;p.immuneTime=60;
        if(memberCase is not ("destroyers" or "reuse" or "pierce" or "sentry") || target==null)return;
        var body=Main.npc.Where(n=>n.active && n.type==NPCID.TheDestroyerBody && n.realLife==target.whoAmI).Take(2).ToArray();
        if(body.Length!=2)return;
        farSegment??=body[0];nearSegment??=body[1];
        if(memberCase=="reuse" && shots.Count>0 && replacement==null)
        {
            int oldSlot=farSegment.whoAmI;
            farSegment.active=false;
            int newSlot=NPC.NewNPC(p.GetSource_Misc("unrelated_same_slot"),(int)p.Center.X-320,(int)p.Center.Y,
                NPCID.TheDestroyerBody,Start:oldSlot);
            if(newSlot!=oldSlot)throw new InvalidOperationException("Fixture did not reuse the original NPC slot");
            replacement=Main.npc[newSlot];
        }
        // Preserve native spawn relations, health routing, weapon cycles and
        // projectile flight; control only the test geometry after segmentation.
        foreach(var n in Main.ActiveNPCs)
        {
            if(n.type is <134 or >136)continue;
            n.aiStyle=-1;n.noGravity=true;n.velocity=Vector2.Zero;
            int dx=n==farSegment || n==replacement?-320:n==nearSegment && memberCase=="pierce"?-140:
                n==nearSegment && shots.Count>0?35:n==foreignRoot?-160:-500;
            n.Center=new Vector2(p.Center.X+dx,p.Center.Y);
        }
        if(replacement!=null && Main.projectile.Any(q=>q.active && q.owner==p.whoAmI && q.Hitbox.Intersects(replacement.Hitbox)))
            crossedReplacement=true;
    }
    private static void MemberRequest(Player p,string mode)
    {
        object reply;
        if(mode=="members-kit")
        {
            p.inventory[2].SetDefaults(ItemID.RubyStaff);
            p.inventory[3].SetDefaults(ItemID.StaffoftheFrostHydra);
            ManaPool(p,200,200);
            reply=new{ready=true};
        }
        else if(mode=="members-cut")
        {
            NPC n=memberCase switch {
                "eater"=>Main.npc.Where(n=>n.active && n.type==14).Skip(8).First(),
                "skeleton"=>Main.npc.First(n=>n.active && n.type==36),
                "twins" when !target.active=>Main.npc.First(n=>n.active && n.type==126),
                _=>target
            };
            var reference=DefenseIdentity(p,n);
            int damage=n.StrikeNPC(new NPC.HitInfo{Damage=999999});
            reply=new{reference,damage,n.active};
        }
        else if(mode=="members-state")
            reply=new{tick=Main.GameUpdateCount,target.life,target.active,target.dontTakeDamage,foreignLife=foreignRoot?.life,
                foreignRef=foreignRoot==null?null:NativeCombatWorld.Reference(Bound(p),foreignRoot),shots,projectileHits,hitTrace,
                replacement=replacement==null?null:DefenseIdentity(p,replacement),crossedReplacement,
                activeMembers=Main.npc.Where(n=>n.active&&Keeps(n)).GroupBy(n=>(n.type,n.realLife,BossMembers.Id(n).Family))
                    .Select(g=>new{g.Key.type,g.Key.realLife,g.Key.Family,count=g.Count()})};
        else
        {
            memberCase=mode.Substring("members-".Length);
            Main.dayTime=false;Main.time=1000;
            NPC Spawn(int type,int dx)
            {
                int slot=NPC.NewNPC(p.GetSource_Misc("isolated_multi_root"),(int)p.Center.X+dx,(int)p.Center.Y,type,Start:2,Target:p.whoAmI);
                return Main.npc[slot];
            }
            target=Spawn(memberCase switch {"destroyers" or "reuse" or "pierce" or "sentry"=>134,"twins" or "ambiguous"=>125,"skeleton"=>35,
                "moon"=>398,"eater"=>13,_=>throw new ArgumentException(mode)},-220);
            if(target.type==134){foreignRoot=Spawn(134,260);p.inventory[54].stack=1;}
            if(memberCase is "twins" or "ambiguous")Spawn(126,250);
            if(memberCase=="ambiguous"){Spawn(125,280);Spawn(126,-280);}
            reply=DefenseIdentity(p,target);
        }
        File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(reply));
    }
}
