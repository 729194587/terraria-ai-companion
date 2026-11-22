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
    // Exact approved wire edits. Everything else (including all diagnostic
    // strings, unknown flags, counts, coordinates and JSON types) must match.
    private static JsonObject CompactContract(JsonObject original)
    {
        var expected=original.DeepClone().AsObject();
        Check(expected["schemaVersion"].GetValue<int>()==1,"pinned source is pre-compaction schema 1");
        expected["schemaVersion"]=2;
        var pixels=expected["actor"]["boundsWorldPixels"];
        expected["actor"]["bodyTileBounds"]=JsonSerializer.SerializeToNode(BodyTileBounds.FromPixels(
            pixels["x"].GetValue<double>(),pixels["y"].GetValue<double>(),pixels["width"].GetValue<double>(),pixels["height"].GetValue<double>()),Json);
        void Remove(JsonObject node,params string[] names)
        {foreach(string name in names){Check(node[name] is JsonValue value&&value.TryGetValue<string>(out _),"removed field is explanatory text: "+name);node.Remove(name);}}
        Remove(expected["coverage"].AsObject(),"objectScope","treeIdentity","candidateScope");
        if(expected["map"] is JsonObject map)Remove(map,"legend","priority");
        if(expected["geometry"] is JsonObject geometry)Remove(geometry,"legend","meaning");
        if(expected["hazards"] is JsonObject hazards)Remove(hazards,"meaning");
        if(expected["objects"] is JsonObject objects)foreach(var item in objects["items"].AsArray())
        {
            Remove(item.AsObject(),"grouping");
            if(item["kind"].GetValue<string>()=="workbench")
            {
                var anchor=item["details"]["anchorTile"];
                item["furniture"]=JsonSerializer.SerializeToNode(new{category="workbench",
                    footprint=new{x=anchor["x"].GetValue<int>(),y=anchor["y"].GetValue<int>(),width=2,height=1},
                    origin=new{x=0,y=0},placementAnchor=anchor},Json);
                item["complete"]=item["observedTileCount"].GetValue<int>()==2;
            }
            if(item["details"] is JsonObject details&&details.ContainsKey("identity"))Remove(details,"identity");
        }
        foreach(string name in new[]{"landingRegions","placementCandidates","objectPlacementCandidates"})
        {
            if(expected[name] is not JsonObject collection)continue;
            string validation=null;
            foreach(var item in collection["items"].AsArray())
            {
                var text=item["validation"].GetValue<string>();
                Check(validation==null||validation==text,"only identical candidate validation can be consolidated");
                validation=text;Remove(item.AsObject(),"validation");
            }
            if(validation!=null)collection["validation"]=validation;
        }
        return expected;
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
        var before=Normalize(BaselineServerSceneReader.Read(actor,identity,Args()));
        var legacy=CompactContract(before);
        var full=Normalize(ServerSceneReader.Read(actor,identity,Args()));
        Check(full["actor"]["bodyTileBounds"].ToJsonString()=="{\"minX\":22,\"minY\":17,\"maxX\":23,\"maxY\":19}","actual reader emits complete body tile bounds");
        Check(full["actor"]["mapTile"]["y"].GetValue<int>()==18,"mapTile remains center rather than body top left");
        Check(JsonNode.DeepEquals(legacy,full),"default equals pinned cff5ae7 after only the declared text edits and schema version change");
        Check(JsonNode.DeepEquals(full,Normalize(ServerSceneReader.Read(actor,identity,Args(Names)))) ,"explicit all and default are identical snapshots");
        var cases=Names.Select(n=>new[]{n}).Concat(new[]{new[]{"geometry","landingRegions","hazards"},new[]{"objects","map"},new[]{"placementCandidates","objectPlacementCandidates"},Names}).ToArray();
        int fullBytes=JsonSerializer.SerializeToUtf8Bytes(full,Json).Length;
        int beforeBytes=JsonSerializer.SerializeToUtf8Bytes(before,Json).Length;
        var sizes=new Dictionary<string,int>{{"all_default",fullBytes}};
        var comparisons=new Dictionary<string,object>{{"all_default",new{before=beforeBytes,after=fullBytes,saved=beforeBytes-fullBytes}}};
        foreach(var selection in cases)
        {
            var oldResponse=Normalize(BaselineServerSceneReader.Read(actor,identity,Args(selection)));
            SceneReaderFixture.ResetCounts();AtomicParameters.Validate("observe_scene",Args(selection),true);
            var response=Normalize(ServerSceneReader.Read(actor,identity,Args(selection)));
            Check(JsonNode.DeepEquals(CompactContract(oldResponse),response),"every selected response is semantically equal after allowlisted text edits");
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
            int oldBytes=JsonSerializer.SerializeToUtf8Bytes(oldResponse,Json).Length;
            Check(bytes<oldBytes,"compaction reduces measured JSON bytes for this fixed selection");
            comparisons[string.Join("+",selection)]=new{before=oldBytes,after=bytes,saved=oldBytes-bytes};
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
        Variant("truncated",limitedArgs);
        // Cover different shapes and a world boundary in the actual reader.
        for(int y=0;y<29;y++)cells[0,y]=new(new("out_of_world"),'?');
        cells[14,5]=new(new("read",true,1,Solid:true,HalfBlock:true),'h');
        for(int slope=1;slope<=4;slope++)cells[14+slope,5]=new(new("read",true,1,Solid:true,Slope:slope),(char)('0'+slope));
        cells[19,5]=new(new("read",true,19,SolidTop:true),'=');cells[20,5]=new(new("read",true,19,SolidTop:true,Slope:1),'a');
        cells[21,5]=new(new("read",true,19,SolidTop:true,Slope:2),'b');
        for(int liquid=0;liquid<4;liquid++)cells[14+liquid,6]=new(new("read",LiquidType:liquid,LiquidAmount:100),'.');
        Variant("shapes_liquids_boundary",Args());
        var featureMap=Normalize(ServerSceneReader.Read(actor,identity,Args(new[]{"map","geometry"})));
        Check(featureMap["geometry"]["rows"][5].GetValue<string>().Substring(14,8)=="h1234=ab","half-block, slopes and platforms keep their distinct glyphs");
        Check(featureMap["map"]["rows"][6].GetValue<string>().Substring(14,4)=="~LHS"&&featureMap["map"]["rows"][0].GetValue<string>()[0]=='?',"liquid types and unknown world boundary remain distinct");
        cells[5,9]=new(new("read",true,5),'.');cells[9,10]=new(new("read",true,48,Solid:true),'#');
        for(int liquid=0;liquid<4;liquid++)cells[14+liquid,6]=new(new("read"),'.');
        Main.npc=Array.Empty<NPC>();Main.projectile=Array.Empty<Projectile>();
        Variant("empty_hazards_with_unknown",Args(new[]{"hazards"}));
        var empty=Normalize(ServerSceneReader.Read(actor,identity,Args(new[]{"hazards"})));
        Check(empty["hazards"]["tiles"]["items"].AsArray().Count==0&&empty["hazards"]["entities"]["items"].AsArray().Count==0&&empty["coverage"]["unknownCells"].GetValue<int>()>0,"empty hazards retain scope counts and unknown information, never imply global safety");
        for(int y=0;y<29;y++)for(int x=0;x<45;x++)cells[x,y]=new(new("unloaded"),'?');
        Variant("empty_candidates_unknown",Args());
        var unknownCandidates=Normalize(ServerSceneReader.Read(actor,identity,Args()));
        foreach(string name in new[]{"landingRegions","placementCandidates","objectPlacementCandidates"})
            Check(unknownCandidates[name]["matchedCount"].GetValue<int>()==0&&!unknownCandidates[name].AsObject().ContainsKey("validation"),"empty candidate collection has no redundant validation: "+name);
        Check(unknownCandidates["objectPlacementCandidates"]["diagnostics"]["unknownCount"].GetValue<int>()==1232&&unknownCandidates["objectPlacementCandidates"]["diagnostics"]["rejectedCount"].GetValue<int>()==0,"empty candidates preserve unknown versus confirmed exclusion distinction");
        void Variant(string name,JsonElement args)
        {
            var old=Normalize(BaselineServerSceneReader.Read(actor,identity,args));
            var current=Normalize(ServerSceneReader.Read(actor,identity,args));
            Check(JsonNode.DeepEquals(CompactContract(old),current),"retained values/types equal for variant: "+name);
            File.WriteAllText(Path.Combine(output,name+"-before.json"),old.ToJsonString(Json));
            File.WriteAllText(Path.Combine(output,name+"-after.json"),current.ToJsonString(Json));
        }
        File.WriteAllText(Path.Combine(output,"before.json"),before.ToJsonString(Json));
        var oldMap=before["map"]["rows"].AsArray().Select(r=>r.GetValue<string>()).ToArray();
        var oldGeometry=before["geometry"]["rows"].AsArray().Select(r=>r.GetValue<string>()).ToArray();
        int Runs(string[] rows)=>rows.Select((r,i)=>i==0||r!=rows[i-1]?1:0).Sum();
        File.WriteAllText(Path.Combine(output,"audit.json"),JsonSerializer.Serialize(new{fixtureOnly=true,
            equalRows=oldMap.Zip(oldGeometry).Count(pair=>pair.First==pair.Second),totalRows=29,
            equalCells=oldMap.Zip(oldGeometry).Sum(pair=>pair.First.Zip(pair.Second).Count(chars=>chars.First==chars.Second)),totalCells=1305,
            mapRowRuns=Runs(oldMap),geometryRowRuns=Runs(oldGeometry),
            decision="keep both row arrays: entity/liquid overlays are non-injective; retain direct row coordinates, no RLE decoder"},new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(output,"compaction-bytes.json"),JsonSerializer.Serialize(new{fixture="actual readers and separate records pinned to cff5ae7; normalize sceneId/elapsedMilliseconds only; data object UTF-8",comparisons},new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(output,"byte-sizes.json"),JsonSerializer.Serialize(new{fixture="actual ServerSceneReader; deterministic 45x29 engine stubs; normalized sceneId/elapsedMilliseconds only",fullBytes,sizes},new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(output,"full.json"),full.ToJsonString(Json));
        Console.WriteLine($"Scene reader layers/compaction: {checks} checks; actual reader vs pinned cff5ae7, engine stubs (not native acceptance).");
        foreach(var size in sizes)Console.WriteLine($"UTF-8 bytes {size.Key}: {size.Value}; saved {fullBytes-size.Value}");
        Console.WriteLine(JsonSerializer.Serialize(comparisons));
    }
    private static void TileIDSetup(){Terraria.ID.TileID.Sets.IsATreeTrunk[5]=true;}
}
