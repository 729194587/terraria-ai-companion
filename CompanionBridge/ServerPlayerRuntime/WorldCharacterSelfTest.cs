using System;
using System.IO;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace VirtualPlayerEngineProbe;

// Only the explicitly marked private headless fixture may inject item states.
internal static class WorldCharacterSelfTest
{
    private static bool ran, cycling;
    private static long respawnGeneration;
    private static string previousHash;
    private static bool Enabled=>Program.LaunchParameters.ContainsKey("-worldcharacterselftest")
        &&File.Exists(Path.Combine(Main.SavePath,"isolated-world-character-fixture"));
    private static int Round=>int.Parse(Program.LaunchParameters["-worldcharacterround"]);
    private static string Pointer=>Path.Combine(new CharacterFiles(Main.SavePath).DirectoryFor(WorldCharacterPersistence.ProfileId),"world.json");
    private static void Check(bool ok,string reason){if(!ok)throw new IOException(reason);}
    internal static void Tick()
    {
        if(!Enabled||ran||!WorldCharacterPersistence.Ready||ProbeSession.Actor==null)return;
        ran=true;
        try
        {
            var p=ProbeSession.Actor;string run=ProbeSession.WorldSession;long generation=ProbeSession.ActorGeneration;
            ModContent.GetInstance<ProbeSystem>().OnWorldLoad();WorldCharacterPersistence.Start();
            Check(run==ProbeSession.WorldSession&&generation==ProbeSession.ActorGeneration&&ReferenceEquals(p,ProbeSession.Actor),"Repeated world load/start changed actor identity.");
            if(Round==1)
            {
                p.inventory[6].SetDefaults(ItemID.Wood);p.inventory[6].stack=173;p.inventory[6].favorited=true;
                p.inventory[1].SetDefaults(ItemID.IronPickaxe);Check(p.inventory[1].Prefix(PrefixID.Light),"Native prefix rejected.");
                p.armor[0].SetDefaults(ItemID.IronHelmet);p.dye[0].SetDefaults(ItemID.RedDye);
                p.selectedItem=6;
                AiMapRuntime.Map.Remember(1,1,3);
                if (Program.LaunchParameters.ContainsKey("-worldcharactercycle"))
                {
                    string id = WorldCharacterPersistence.ProfileId;
                    cycling = true;
                    try { ProbeSession.Finish("fixture explicit despawn", keepReservation: true); }
                    finally { cycling = false; }
                    Check(ProbeSession.Actor == null && !WorldCharacterPersistence.Ready, "Despawn retained the binding.");
                    WorldCharacterPersistence.SpawnRequested();
                    var restored = ProbeSession.Actor;
                    respawnGeneration = ProbeSession.ActorGeneration;
                    Check(WorldCharacterPersistence.Ready && restored != null && !ReferenceEquals(p, restored)
                        && respawnGeneration > generation && restored.whoAmI == p.whoAmI
                        && id == WorldCharacterPersistence.ProfileId && restored.inventory[6].stack == 173,
                        "Explicit respawn failed to restore the world profile on a new binding.");
                }
            }
            else
            {
                Check(p.inventory[6].type==ItemID.Wood&&p.inventory[6].stack==173&&p.inventory[6].favorited
                    &&p.inventory[1].type==ItemID.IronPickaxe&&p.inventory[1].prefix==PrefixID.Light
                    &&p.armor[0].type==ItemID.IronHelmet&&p.dye[0].type==ItemID.RedDye&&p.selectedItem==6
                    &&p.GetModPlayer<ProbePlayer>().PersistentMarker=="world-ai:"+WorldCharacterPersistence.ProfileId,
                    "Native world restore lost items/equipment/prefix/favorite/hotbar/identity.");
                Check(AiMapRuntime.Map.Get(1,1)==3,"Map did not restore with native character generation.");
                Check(!p.dead&&p.statLife>0,"Restored character retained transient death.");
            }
            if(Round==2)NativeSurvival.Debug(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,kill:true);
            if(Round==4)
            {
                previousHash=CharacterFiles.Hash(Pointer);
                p.inventory[6].stack=999;
                File.SetAttributes(Pointer,File.GetAttributes(Pointer)|FileAttributes.ReadOnly);
            }
            Netplay.Disconnect=true; // The REAL pre-clearWorld/simulation-finally path must save.
        }
        catch(Exception e){Report("fixture-failed",ProbeSession.Actor,e.Message);Netplay.Disconnect=true;}
    }
    internal static void Saved(Player p)
    {if(Enabled&&ran&&!cycling)Report(Round==4?"unexpected-save-success":"world-character-native-passed",p,null);}
    internal static void SaveFailed(Player p,Exception error)
    {
        if(!Enabled||!ran)return;
        bool retained=Round==4&&ReferenceEquals(p,ProbeSession.Actor)&&p.active&&CharacterFiles.Hash(Pointer)==previousHash
            &&!p.controlUseItem&&!p.controlLeft&&!p.controlRight&&!p.controlJump;
        Report(retained?"save-failed-retained":"unexpected-save-failure",p,error.Message);
    }
    internal static void StartFailed(Exception error)
    {if(Enabled){Report(Round==5&&ProbeSession.Actor==null?"corrupt-load-rejected":"unexpected-start-failure",ProbeSession.Actor,error.Message);Netplay.Disconnect=true;}}
    private static void Report(string result,Player p,string error)
    {
        File.WriteAllText(Path.Combine(Main.SavePath,"world-character-result.json"),JsonSerializer.Serialize(new{
            result,round=Round,error,respawnGeneration,world=Main.ActiveWorldFileData?.UniqueId,profileId=WorldCharacterPersistence.ProfileId,
            actorPublished=ProbeSession.Actor!=null,wood=p?.inventory[6].stack,armor=p?.armor[0].type,prefix=p?.inventory[1].prefix,
            selectedItem=p?.selectedItem,marker=p?.GetModPlayer<ProbePlayer>().PersistentMarker,
            dead=p?.dead,life=p?.statLife,ownerThread=Program.IsMainThread}));
    }
}
