using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using VirtualPlayerEngineProbe;

public static class AiMapTests
{
    public static void Restore(string root)
    {var map=AiMap.Load(Path.Combine(root,"cross.bin"),Guid.Parse(File.ReadAllText(Path.Combine(root,"identity"))),"character-a",8400,2400);if(map.Get(1,0)!=4)throw new Exception("cross-process map differs");Console.WriteLine("AI map cross-process restore passed.");}
    public static void Run(string root)
    {
        int checks=0;void Check(bool b,string m){if(!b)throw new Exception(m);checks++;}
        void Reject(Action a){bool rejected=false;try{a();}catch(IOException){rejected=true;}Check(rejected,"corruption/identity accepted");}
        object Query(AiMap m,object a)=>m.Query(JsonSerializer.SerializeToElement(a),0,0);
        JsonElement Data(object a)=>JsonSerializer.SerializeToElement(a);
        var world=Guid.NewGuid();var map=new AiMap(world,"character-a",8400,2400);
        var unknown=Data(Query(map,new{mode="region",centerX=1,centerY=1,width=2,height=2,cellSize=2}));
        Check(unknown.GetProperty("rows")[0].GetString()=="?"&&unknown.GetProperty("exploredRatio").GetDouble()==0,"unknown leaked");
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
