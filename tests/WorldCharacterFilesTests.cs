using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using VirtualPlayerEngineProbe;

public static class WorldCharacterFilesTests
{
    private static int checks;
    private static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
    private static void Reject(Action action,string why)
    {bool failed=false;try{action();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException){failed=true;}Check(failed,why);}
    public static void Run(string root)
    {
        string engine=new('A',64),state=new('B',64);
        Guid a=Guid.NewGuid(),b=Guid.NewGuid();
        var native=new CharacterFiles(root);
        using var first=new WorldCharacterFiles(root,a,engine);first.Acquire();
        Check(first.Read()==null,"genuinely new world has no profile");
        using(var competing=new WorldCharacterFiles(root,a,engine))Reject(competing.Acquire,"exclusive lease blocks concurrent world profile owners");
        string Stage(string id,string value)
        {string path=native.Begin(id);File.WriteAllText(path,value);File.WriteAllText(Path.ChangeExtension(path,".tplr"),value+"-mod");return path;}
        var saved=first.Commit(Stage(first.CharacterId,"wood=173;armor=iron;prefix=light"),state,6,null);
        Check(first.Read()==saved&&File.ReadAllText(first.Open(saved)).Contains("wood=173"),"world pointer restores exact committed bytes and slot");
        using(var other=new WorldCharacterFiles(root,b,engine))
        {
            other.Acquire();Check(other.CharacterId!=first.CharacterId&&other.Read()==null,"different UUIDs isolate worlds even when names are identical (names are not inputs)");
            var otherSaved=other.Commit(Stage(other.CharacterId,"wood=2"),state,1,null);
            Check(other.Read().SelectedItem==1&&first.Read().SelectedItem==6,"world hotbar and native generations do not cross");
            Reject(()=>other.Open(saved),"wrong-world manifest cannot open another character");
        }
        string pointer=Path.Combine(native.DirectoryFor(first.CharacterId),"world.json");
        byte[] valid=File.ReadAllBytes(pointer);
        Reject(()=>first.Commit(Stage(first.CharacterId,"wood=999"),state,3,saved,()=>throw new IOException("injected publish failure")),"save publication failure is reported");
        Check(valid.SequenceEqual(File.ReadAllBytes(pointer))&&first.Read()==saved,"failed save cannot destroy prior committed pair or hotbar");
        Check(File.ReadAllText(first.Open(saved)).Contains("wood=173"),"prior immutable native generation retained");
        foreach(string field in new[]{"Version","WorldUid","CharacterId","EngineHash","SelectedItem"})
        {
            var data=JsonNode.Parse(valid).AsObject();
            data[field]=field=="Version"?JsonValue.Create(999):field=="SelectedItem"?JsonValue.Create(10):JsonValue.Create("bad");
            File.WriteAllText(pointer,data.ToJsonString());
            Reject(()=>first.Read(),"invalid/unsupported manifest rejects without new profile: "+field);
            byte[] corrupt=File.ReadAllBytes(pointer);
            Reject(()=>first.Commit(Stage(first.CharacterId,"replacement"),state,0,saved),"save cannot overwrite invalid pointer: "+field);
            Check(corrupt.SequenceEqual(File.ReadAllBytes(pointer)),"rejected pointer remains unchanged");
            File.WriteAllBytes(pointer,valid);
        }
        File.WriteAllText(pointer,"{broken");Reject(()=>first.Read(),"malformed JSON cannot become starter profile");File.WriteAllBytes(pointer,valid);
        var missing=JsonNode.Parse(valid).AsObject();missing.Remove("SelectedItem");File.WriteAllText(pointer,missing.ToJsonString());
        Reject(()=>first.Read(),"missing required zero-valued fields are not silently defaulted");File.WriteAllBytes(pointer,valid);
        string nativePath=first.Open(saved);
        File.AppendAllText(nativePath,"tampered");Reject(()=>first.Read(),"corrupt native file rejected before native deserialization");
        Reject(()=>first.Commit(Stage(first.CharacterId,"new starter"),state,0,null),"corrupt native pair cannot be overwritten even with absent-predecessor claim");
        File.Delete(nativePath);Reject(()=>first.Read(),"missing native file is corruption, not a new character");
        Reject(()=>first.Commit(Stage(first.CharacterId,"new starter"),state,0,null),"missing native pair cannot cause pointer replacement");
        using(var orphan=new WorldCharacterFiles(root,Guid.NewGuid(),engine))
        {orphan.Acquire();Stage(orphan.CharacterId,"partial first save");Reject(()=>orphan.Read(),"missing pointer with staged data is incomplete, not new");}
        Guid upgradeWorld=Guid.NewGuid();
        string targetEngine=new('C',64);
        WorldCharacterManifest old;
        string upgradePointer,oldNative;
        byte[] oldPointerBytes;
        using(var legacy=new WorldCharacterFiles(root,upgradeWorld,engine))
        {
            legacy.Acquire();
            oldNative=Stage(legacy.CharacterId,"legacy");
            var mapPath=Path.Combine(Path.GetDirectoryName(oldNative),"map.bin");
            File.WriteAllText(mapPath,"known tiles");
            old=legacy.Commit(oldNative,state,6,null,mapHash:CharacterFiles.Hash(mapPath));
            upgradePointer=Path.Combine(native.DirectoryFor(legacy.CharacterId),"world.json");
            oldPointerBytes=File.ReadAllBytes(upgradePointer);
        }
        using(var target=new WorldCharacterFiles(root,upgradeWorld,targetEngine))
        {
            target.Acquire();
            Reject(()=>target.Read(),"ordinary target startup rejects an old pointer");
            Reject(()=>target.Migrate(target.Read(engine),engine,(_,_)=>throw new IOException("read-back failed")),
                "migration rejects a failed read-back");
            Check(oldPointerBytes.SequenceEqual(File.ReadAllBytes(upgradePointer)),"failed migration retains the pointer bytes");
        }
        using(var target=new WorldCharacterFiles(root,upgradeWorld,targetEngine))
        {
            target.Acquire();
            var upgraded=target.Migrate(target.Read(engine),engine,(path,candidate)=>
            {
                Check(path!=oldNative&&File.ReadAllText(path)=="legacy"&&candidate.Native.StateHash==old.Native.StateHash,
                    "verification sees the staged copy and original state fingerprint");
            });
            Check(target.Read()==upgraded&&upgraded.SelectedItem==old.SelectedItem&&upgraded.MapHash==old.MapHash
                && File.ReadAllText(target.OpenMap(upgraded))=="known tiles"&&CharacterFiles.Hash(oldNative)==old.Native.PlrHash,
                "retry uses a new generation and retains original character, map and slot");
        }
        using(var legacy=new WorldCharacterFiles(root,upgradeWorld,engine))
        {
            legacy.Acquire();
            Check(legacy.Rollback(targetEngine,(_,_)=>{})==old&&legacy.Read()==old,"explicit rollback restores the previous verified generation");
        }
        var session=new WorldCharacterSession();int loads=0,saves=0;
        session.Enter(a,"epoch",()=>loads++);session.Enter(a,"epoch",()=>loads++);
        Check(loads==1&&session.Phase==WorldCharacterPhase.Ready,"duplicate load never recreates/loads actor twice");
        Reject(()=>session.Enter(b,"epoch",()=>loads++),"world change cannot replace an active session");
        session.Exit(()=>saves++);session.Exit(()=>saves++);session.Enter(a,"epoch",()=>loads++);
        Check(loads==1&&saves==1&&session.Phase==WorldCharacterPhase.Saved,"duplicate quit/load callbacks do not save twice or resurrect actor");
        var failed=new WorldCharacterSession();failed.Enter(a,"fail",()=>{});
        Reject(()=>failed.Exit(()=>throw new IOException("disk full")),"failed save remains explicit");
        Reject(()=>failed.Exit(()=>saves++),"failed save is not automatically retried or mislabeled successful");
        var corruptSession=new WorldCharacterSession();Reject(()=>corruptSession.Enter(a,"corrupt",()=>throw new IOException("corrupt")),"restore failure prevents ready state");
        Reject(()=>corruptSession.Enter(a,"corrupt",()=>loads++),"duplicate callback cannot recover corrupt load by creating a starter");
        Check(corruptSession.Phase==WorldCharacterPhase.Failed,"load failure remains terminal");
        Console.WriteLine($"World character storage/lifecycle: {checks} checks; synthetic native bytes, not a native item round-trip.");
    }
}
