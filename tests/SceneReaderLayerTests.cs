using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanionBridge;
using CompanionBridge.ServerIpc;
using Terraria;

public static class SceneReaderLayerTests
{
    private static int checks;
    private static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private static readonly string[] Names={"map","geometry","objects","hazards","landingRegions","placementCandidates","objectPlacementCandidates"};
    private static readonly Dictionary<string,string> Companions=new(){{"objects","objectCounts"},{"placementCandidates","placementMaterial"},{"objectPlacementCandidates","objectPlacementMaterial"}};
    private static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
    private static JsonObject Normalize(object data,bool included=true)
    {
        var node=JsonSerializer.SerializeToNode(data,Json).AsObject();
        node["sceneId"]="fixture:scene";node["sample"]["elapsedMilliseconds"]=0;
        if(!included)node.Remove("includedLayers");return node;
    }
    public static void Run(string output)
    {
        var cells=SceneReaderFixture.Cells=new SceneCell[45,29];
        for(int y=0;y<29;y++)for(int x=0;x<45;x++)cells[x,y]=new(new("read",y>=18,y>=18?1:null,Solid:y>=18),y>=18?'#':'.');
        TileIDSetup();
        for(int y=9;y<14;y++)cells[5,y]=new(new("read",true,5),'.');
        cells[5,9]=new(new("read",true,5,LiquidAmount:100),'.');
        cells[6,9]=new(new("unloaded"),'?');
        cells[6,19]=new(new("read",true,7,Solid:true),'#');cells[7,19]=cells[6,19];
        cells[12,17]=new(new("read",true,18),'.');cells[13,17]=new(new("read",true,18,FrameX:18),'.');
        cells[9,10]=new(new("read",true,48,Solid:true,TouchDamage:20,Hot:true),'!');
        var actor=new Player{position=new(352,272),width=20,height=42,whoAmI=254};
        Main.player=new[]{actor,new Player{position=new(128,176),whoAmI=0}};
        Main.npc=new[]{new NPC{position=new(80,176)},new NPC{position=new(96,176),friendly=true},new NPC{position=new(160,176)}};
        Main.item=new[]{new Item{position=new(80,176)},new Item{position=new(112,176)}};
        Main.projectile=new[]{new Projectile{position=new(80,176)},new Projectile{position=new(144,176)}};
        var identity=new AiReadOnlyProtocol.Identity(actor,"fixture",1,254);
        JsonElement Args(string[] layers=null)=>JsonSerializer.SerializeToElement(layers==null?new Dictionary<string,object>():new(){["layers"]=layers});
        var legacy=Normalize(BaselineServerSceneReader.Read(actor,identity,Args()),false);
        var full=Normalize(ServerSceneReader.Read(actor,identity,Args()));
        var withoutIncluded=full.DeepClone().AsObject();withoutIncluded.Remove("includedLayers");
        Check(JsonNode.DeepEquals(legacy,withoutIncluded),"default preserves every 61bf2d7 field/value except volatile sample/id and added includedLayers");
        Check(JsonNode.DeepEquals(full,Normalize(ServerSceneReader.Read(actor,identity,Args(Names)))) ,"explicit all and default are identical snapshots");
        var cases=Names.Select(n=>new[]{n}).Concat(new[]{new[]{"geometry","landingRegions","hazards"},new[]{"objects","map"},new[]{"placementCandidates","objectPlacementCandidates"},Names}).ToArray();
        int fullBytes=JsonSerializer.SerializeToUtf8Bytes(full,Json).Length;
        var sizes=new Dictionary<string,int>{{"all_default",fullBytes}};
        foreach(var selection in cases)
        {
            SceneReaderFixture.ResetCounts();AtomicParameters.Validate("observe_scene",Args(selection),true);
            var response=Normalize(ServerSceneReader.Read(actor,identity,Args(selection)));
            var selected=selection.ToHashSet();
            Check(response["includedLayers"].AsArray().Select(v=>v.GetValue<string>()).SequenceEqual(Names.Where(selected.Contains)),"includedLayers uses canonical actual selection order");
            var expectedKeys=legacy.Select(p=>p.Key).Where(k=>!Names.Contains(k)&&!Companions.ContainsValue(k)).Concat(new[]{"includedLayers"}).Concat(selection).Concat(Companions.Where(p=>selected.Contains(p.Key)).Select(p=>p.Value)).ToHashSet();
            Check(response.Select(p=>p.Key).ToHashSet().SetEquals(expectedKeys),"unrequested fields really absent; all identity/state/coverage/sample/limits and required companions remain");
            foreach(var key in expectedKeys.Where(k=>k!="includedLayers"))Check(JsonNode.DeepEquals(full[key],response[key]),"selected field equals full observation: "+key);
            Check(SceneReaderFixture.Reads==1305,"base unknown/navigation coverage still samples all cells");
            Check(SceneReaderFixture.Worlds==(selected.Contains("landingRegions")?1:0),"native landing world not created when omitted");
            Check((SceneReaderFixture.LandingChecks>0)==selected.Contains("landingRegions"),"native safe-landing validator only evaluated for requested layer");
            Check(SceneReaderFixture.Definitions==(selected.Contains("objectPlacementCandidates")?1:0),"workbench definition/diagnostic computation only entered when requested");
            Check(SceneReaderFixture.TileMaterials==(selected.Contains("placementCandidates")?1:0)&&SceneReaderFixture.ObjectMaterials==(selected.Contains("objectPlacementCandidates")?1:0),"material checks skipped independently");
            Check((SceneReaderFixture.Anchors>0)==selected.Contains("objects"),"registered object anchor resolution skipped even for map-only");
            Check((SceneReaderFixture.EntityChecks>0)==selected.Overlaps(new[]{"map","objects","hazards"}),"entity classification skipped for geometry/candidate-only requests");
            int bytes=JsonSerializer.SerializeToUtf8Bytes(response,Json).Length;
            if(selection.Length<7)Check(bytes<fullBytes,"measured selected-layer JSON is smaller for fixed fixture");
            sizes[string.Join("+",selection)]=bytes;
        }
        var rows=full["map"]["rows"].AsArray();
        Check(rows[9].GetValue<string>()[5]=='~'&&rows[9].GetValue<string>()[6]=='?',"unknown and tile dangers override entity/projectile/object markers");
        Check(rows[9].GetValue<string>().Substring(7,4)=="$P*!"&&rows[16].GetValue<string>()[22]=='@',"drop/player/projectile/NPC/actor overlay preserved");
        Check(full["objects"]["truncated"].GetValue<bool>()==false&&full["objectPlacementCandidates"]["diagnostics"]["unknownCount"].GetValue<int>()>0,"full snapshot keeps actual object and unknown candidate diagnostics");
        var limitedArgs=JsonSerializer.SerializeToElement(new{layers=new[]{"objects","objectPlacementCandidates"},maxObjects=1,maxPlacements=1});
        var limited=Normalize(ServerSceneReader.Read(actor,identity,limitedArgs));
        Check(limited["objects"]["truncated"].GetValue<bool>()&&limited["objectPlacementCandidates"]["truncated"].GetValue<bool>(),"requested collections keep independent truncation");
        Check(JsonNode.DeepEquals(limited["objectPlacementCandidates"]["diagnostics"],full["objectPlacementCandidates"]["diagnostics"]),"truncation leaves workbench diagnostic counters untouched");
        Check(limited.ContainsKey("objectCounts")&&limited.ContainsKey("objectPlacementMaterial")&&!limited.ContainsKey("placementMaterial"),"companions exactly match the requested candidate/object layers");
        foreach(string invalid in new[]{"{\"layers\":[]}","{\"layers\":[\"bad\"]}","{\"layers\":[\"map\",\"map\"]}","{\"layers\":\"map\"}","{\"layers\":{}}","{\"layers\":[1]}","{\"layers\":null}","{\"layers\":[\"Map\"]}","{\"layers\":[\"map\"],\"extra\":1}","{\"layers\":[\"map\"],\"layers\":[\"objects\"]}"})
        {
            bool rejected=false;try{using var parsed=JsonDocument.Parse(invalid);AtomicParameters.Validate("observe_scene",parsed.RootElement,true);}catch(FormatException){rejected=true;}
            Check(rejected,"illegal scene layers rejected without fallback: "+invalid);
        }
        var plan=SceneLayers.Read(Args(new[]{"map"}));
        foreach(string name in Names.Where(n=>n!="map"))plan.Add(new(),name,()=>throw new Exception("unrequested factory executed"));
        File.WriteAllText(Path.Combine(output,"byte-sizes.json"),JsonSerializer.Serialize(new{fixture="actual ServerSceneReader; deterministic 45x29 engine stubs; normalized sceneId/elapsedMilliseconds only",fullBytes,sizes},new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(output,"full.json"),full.ToJsonString(Json));
        Console.WriteLine($"Scene reader layers: {checks} checks; actual reader vs pinned 61bf2d7, engine stubs (not native acceptance).");
        foreach(var size in sizes)Console.WriteLine($"UTF-8 bytes {size.Key}: {size.Value}; saved {fullBytes-size.Value}");
    }
    private static void TileIDSetup(){Terraria.ID.TileID.Sets.IsATreeTrunk[5]=true;}
}
