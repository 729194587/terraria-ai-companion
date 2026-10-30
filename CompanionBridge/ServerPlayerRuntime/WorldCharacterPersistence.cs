using System;
using System.IO;
using System.Linq;
using Terraria;
using CompanionBridge.ServerIpc;

namespace VirtualPlayerEngineProbe;

// All Player/native IO stays on the audited simulation thread under its Gate.
internal static class WorldCharacterPersistence
{
    private static WorldCharacterSession session=new();
    private static WorldCharacterFiles files;
    private static WorldCharacterManifest committed;
    private static Player actor;
    private static readonly string[] ManualFixtures={"-g31selftest","-survivalselftest","-itemuseselftest","-miningselftest","-placementselftest","-pickupownershipselftest"};
    internal static bool Enabled=>ServerAiSystem.Enabled&&!ManualFixtures.Any(Program.LaunchParameters.ContainsKey);
    internal static string ProfileId=>files?.CharacterId;
    internal static bool Ready=>!Enabled||session.Phase==WorldCharacterPhase.Ready&&World==session.World
        &&ProbeSession.WorldSession==session.Run&&ReferenceEquals(actor,ProbeSession.Actor);
    internal static void Reset()
    {AiMapRuntime.Reset();files?.Dispose();files=null;committed=null;actor=null;session=new();}
    private static Guid World=>Main.ActiveWorldFileData?.UniqueId??Guid.Empty;
    internal static void Start()
    {
        if(!Enabled){ProbeSession.Spawn();return;}
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        if(session.Phase==WorldCharacterPhase.Ready&&!Ready)throw new IOException("World character binding changed; refusing adoption.");
        session.Enter(World,ProbeSession.WorldSession,()=>
        {
            if(ProbeSession.Actor!=null)throw new IOException("Unbound actor already exists; refusing world profile adoption.");
            files=new(Main.SavePath,World,VirtualPlayerEngineProbe.EngineHash);
            files.Acquire();
            try
            {
                committed=files.Read();
                bool restored=committed!=null;
                if(committed!=null)CharacterStore.LoadWorld(files,committed);
                else
                {
                    ProbeSession.Spawn();
                    ProbeSession.Actor.GetModPlayer<ProbePlayer>().PersistentMarker="world-ai:"+files.CharacterId;
                }
                actor=ProbeSession.Actor;
                string mapPath=committed==null?null:files.OpenMap(committed);
                AiMapRuntime.Map=mapPath==null?new AiMap(World,files.CharacterId,Main.maxTilesX,Main.maxTilesY):AiMap.Load(mapPath,World,files.CharacterId,Main.maxTilesX,Main.maxTilesY);
                if(committed?.Version==1)ProbeSession.Log("ai-map-migration",new{from=1,knowledge="empty; legacy profile had no AI map"});
                // Commit new identity before it can receive any IPC actions.
                if(committed==null)Save();
                ProbeSession.Log("world-character-ready",new{world=World,characterId=files.CharacterId,restored});
            }
            catch{if(ProbeSession.Actor==null)files.Dispose();throw;}
        });
    }
    private static void Save()
    {
        if(World!=session.World||ProbeSession.WorldSession!=session.Run||!ReferenceEquals(actor,ProbeSession.Actor))
            throw new IOException("World/actor changed before character save; no write permitted.");
        CharacterStore.SaveWorld(files.CharacterId,(path,state,selected)=>
        {
            string mapPath=Path.Combine(Path.GetDirectoryName(path),"map.bin");
            AiMapRuntime.Map.Save(mapPath);
            committed=files.Commit(path,state,selected,committed,mapHash:CharacterFiles.Hash(mapPath));
        });
        ProbeSession.Log("world-character-saved",new{world=World,characterId=files.CharacterId,committed.Native.Generation});
    }
    internal static void BeforeRetire(Player owned)
    {
        if(!Enabled||session.Phase is WorldCharacterPhase.Empty or WorldCharacterPhase.Loading)return;
        if(owned==null&&actor==null)return; // Failed restore never published a Player.
        if(!ReferenceEquals(owned,actor))throw new IOException("World character retirement ownership changed.");
        try{session.Exit(Save);}
        catch(Exception e){ProbeSession.Log("world-character-save-failed",new{error=e.Message,previousGeneration=committed?.Native.Generation});WorldCharacterSelfTest.SaveFailed(owned,e);throw;}
    }
    internal static void AfterRetire(Player owned)
    {if(ReferenceEquals(owned,actor)&&session.Phase==WorldCharacterPhase.Saved){files?.Dispose();WorldCharacterSelfTest.Saved(owned);}}
}
