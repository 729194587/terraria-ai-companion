using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using VirtualPlayerEngineProbe;

public static class AiMapTests
{
    public static void Restore(string root)
    {var map=AiMap.Load(Path.Combine(root,"cross.bin"),Guid.Parse(File.ReadAllText(Path.Combine(root,"identity"))),"character-a",8400,2400);if(map.Get(1,0)!=4 || !map.Visited(55,10) || map.Visited(25,10) || map.WalkedNear(55,10))throw new Exception("cross-process map differs");Console.WriteLine("AI map cross-process restore passed.");}
    public static void Run(string root)
    {
        int checks=0;void Check(bool b,string m){if(!b)throw new Exception(m);checks++;}
        void Reject(Action a){bool rejected=false;try{a();}catch(IOException){rejected=true;}Check(rejected,"corruption/identity accepted");}
        object Query(AiMap m,object a)=>m.Query(JsonSerializer.SerializeToElement(a),0,0);
        JsonElement Data(object a)=>JsonSerializer.SerializeToElement(a);
        var world=Guid.NewGuid();var map=new AiMap(world,"character-a",8400,2400);
        map.RecordFeet(5,10);map.RecordFeet(55,10);
        Check(map.WalkedNear(6,10)&&!map.WalkedNear(25,10)&&!map.WalkedNear(5,14),"observed footsteps, no interpolated path or stacked tunnel");
        Check(map.UnknownAround(5,10)==81 && map.UnknownAround(0,0)==25,"visits independent of known terrain; clipped map edges");
        var visit=Data(Query(map,new{mode="region",width=64,height=16,cellSize=1,includeVisits=true}));
        var bits=Convert.FromBase64String(visit.GetProperty("visitBits").GetString());
        Check(visit.GetProperty("visitedCount").GetInt32()==2 && bits[(10*64+55)/8]==128,"visit bitmap packing");
        string legacy=Path.Combine(root,"v1.bin");
        using(var writer=new BinaryWriter(File.Create(legacy)))
        {
            writer.Write("CompanionBridge/AiMap");writer.Write(1);writer.Write(world.ToString("N"));writer.Write("legacy");
            writer.Write(64);writer.Write(1);var raw=new byte[64];raw[31]=3;writer.Write(raw);
        }
        var prior=AiMap.Load(legacy,world,"legacy",64,1);
        Check(prior.Get(31,0)==3 && prior.Get(32,0)==0 && prior.Revision==0,"v1 migration");
        prior.BeginSample();prior.Remember(32,0,1,4);prior.Remember(33,0,1,4);prior.EndSample();
        long changed=prior.Revision;
        prior.Remember(32,0,1,5);
        var metadata=Data(Query(prior,new{mode="region",width=64,height=1,cellSize=1,includeChunks=true})).GetProperty("chunks");
        Check(changed==1 && prior.Revision==changed && prior.ChunkRevision(31,0)==0 &&
            metadata[1].GetProperty("lastSeenTick").GetInt64()==5 && metadata[1].GetProperty("knownCount").GetInt32()==2,"chunk age or revision");
        string upgraded=Path.Combine(root,"upgraded.bin");prior.Save(upgraded);
        string v2=Path.Combine(root,"v2.bin");
        using(var writer=new BinaryWriter(File.Create(v2)))
        {
            writer.Write("CompanionBridge/AiMap");writer.Write(2);writer.Write(world.ToString("N"));writer.Write("legacy");
            writer.Write(64);writer.Write(1);var raw=new byte[64];raw[31]=3;writer.Write(raw);
            writer.Write(7L);writer.Write(1);writer.Write(0);writer.Write(0);writer.Write(6L);
            writer.Write(world.ToByteArray());writer.Write(9L);
        }
        var converted=Path.Combine(root,"converted.bin");
        AiMap.Load(v2,world,"legacy",64,1).Save(converted);
        using(var header=new BinaryReader(File.OpenRead(converted)))
            Check(header.ReadString()=="CompanionBridge/AiMap" && header.ReadInt32()==4,"v2 was not upgraded");
        string v3=Path.Combine(root,"v3.bin");
        var packed=File.ReadAllBytes(converted);
        using(var stream=new MemoryStream(packed))
        using(var reader=new BinaryReader(stream))
        {
            reader.ReadString();
            BitConverter.GetBytes(3).CopyTo(packed,stream.Position);
        }
        File.WriteAllBytes(v3,packed[..^sizeof(int)]); // v3 ends after the chunk metadata.
        var oldCompressed=AiMap.Load(v3,world,"legacy",64,1);
        Check(oldCompressed.Get(31,0)==3 && oldCompressed.Revision==7 && oldCompressed.VisitedCount==0,
            "compressed v3 migration manufactured visits or lost map contents");
        var migrated=AiMap.Load(converted,world,"legacy",64,1);
        Check(migrated.Get(31,0)==3 && migrated.Revision==7 && migrated.ChunkRevision(31,0)==6,"v2 migration lost state");
        var reload=AiMap.Load(upgraded,world,"legacy",64,1);
        Check(reload.Revision==1 && reload.ChunkRevision(32,0)==1 && reload.Get(31,0)==3,"metadata restore");
        var unknown=Data(Query(map,new{mode="region",centerX=1,centerY=1,width=2,height=2,cellSize=2}));
        Check(unknown.GetProperty("rows")[0].GetString()=="?"&&unknown.GetProperty("exploredRatio").GetDouble()==0,"unknown leaked");
        Check(!unknown.TryGetProperty("chunks",out _)&&!unknown.TryGetProperty("mapRevision",out _),"legacy response changed");
        for(int y=0;y<2;y++)for(int x=0;x<2;x++)map.Remember(x,y,2);
        map.Remember(1,0,1);
        var mixed=Data(Query(map,new{mode="region",centerX=1,centerY=1,width=2,height=2,cellSize=2}));
        Check(mixed.GetProperty("rows")[0].GetString()==":","one-cell opening disappeared");
        var zoom=Data(Query(map,new{mode="region",centerX=1,centerY=1,width=2,height=2,cellSize=1}));
        Check(zoom.GetProperty("rows")[0].GetString()=="# ","zoom lost structure");
        map.Remember(1,0,4);Check(Data(Query(map,new{mode="region",width=2,height=2,cellSize=2})).GetProperty("rows")[0].GetString()=="~","liquid lost");
        var overview=Data(Query(map,new{mode="overview"}));
        Check(overview.GetProperty("rows").GetArrayLength()<=48&&overview.GetProperty("rows")[0].GetString().Length<=128,"overview limits");
        Check(new AiMap(world,"character-b",8400,2400).Get(0,0)==0,"character shared memory");
        Check(new AiMap(Guid.NewGuid(),"character-a",8400,2400).Get(0,0)==0,"world shared memory");
        string path=Path.Combine(root,"map.bin");map.Save(path);
        Check(new FileInfo(path).Length<100000,"sparse map saved at world size");
        map.Save(Path.Combine(root,"cross.bin"));File.WriteAllText(Path.Combine(root,"identity"),world.ToString());
        var restored=AiMap.Load(path,world,"character-a",8400,2400);Check(restored.Get(1,0)==4&&restored.Get(8000,1000)==0,"restore differs");
        Reject(()=>AiMap.Load(path,Guid.NewGuid(),"character-a",8400,2400));Reject(()=>AiMap.Load(path,world,"other",8400,2400));
        var bytes=File.ReadAllBytes(path);File.WriteAllBytes(path,bytes[..30]);Reject(()=>AiMap.Load(path,world,"character-a",8400,2400));
        // Atomic pointer couples map and native generation, including failure before publication.
        using var files=new WorldCharacterFiles(root,world,new string('A',64));files.Acquire();
        var native=new CharacterFiles(root);
        string Stage(){string p=native.Begin(files.CharacterId);File.WriteAllText(p,"native");File.WriteAllText(Path.ChangeExtension(p,".tplr"),"mod");return p;}
        string staged=Stage();string mp=Path.Combine(Path.GetDirectoryName(staged),"map.bin");map.Save(mp);
        var committed=files.Commit(staged,new string('B',64),0,null,mapHash:CharacterFiles.Hash(mp));
        Check(files.Read()==committed&&files.OpenMap(committed)==mp,"map generation not committed");
        File.WriteAllText(mp,"broken");Reject(()=>files.Read());
        Check(File.Exists(staged),"corruption deleted evidence");
        Console.WriteLine($"AI map: {checks} checks passed.");
    }
}
