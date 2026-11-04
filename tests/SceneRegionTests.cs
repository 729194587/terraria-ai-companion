using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanionBridge;
using CompanionBridge.ServerIpc;
using Terraria;

public static class SceneRegionTests
{
    private static int checks;
    private static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private static void Check(bool b,string why){if(!b)throw new Exception(why);checks++;}
    private static JsonElement Args(string s)=>JsonSerializer.Deserialize<JsonElement>(s);
    private static JsonObject Node(object value)
    {
        var n=JsonSerializer.SerializeToNode(value,Json).AsObject();n["sceneId"]="normalized";n["sample"]["elapsedMilliseconds"]=0;return n;
    }
    private static JsonNode At(JsonNode grid,int x,int y)
    {
        var row=grid["rows"][y];int index;
        if(row["encoding"].GetValue<string>()=="hex_indices")
        {
            int digits=row["indexDigits"].GetValue<int>();
            index=Convert.ToInt32(row["data"].GetValue<string>().Substring(x*digits,digits),16);
            return grid["palette"][index];
        }
        var data=row["data"].AsArray();
        if(row["encoding"].GetValue<string>()=="indices")index=data[x].GetValue<int>();
        else{int end=0,i=0;for(;i<data.Count;i+=2){end+=data[i].GetValue<int>();if(x<end)break;}index=data[i+1].GetValue<int>();}
        return grid["palette"][index];
    }
    public static void Run(string output)
    {
        var actor=new Player{position=new(350,262),width=20,height=42};
        Main.player=new[]{actor};Main.npc=Array.Empty<NPC>();Main.item=Array.Empty<Item>();Main.projectile=Array.Empty<Projectile>();
        var identity=new AiReadOnlyProtocol.Identity(actor,"world",7,0);
        SceneReaderFixture.Cells=new SceneCell[45,29];
        for(int y=0;y<29;y++)for(int x=0;x<45;x++)SceneReaderFixture.Cells[x,y]=new(new("read",y>=18,y>=18?1:null,Solid:y>=18),y>=18?'#':'.');
        var localCases=new[]{"{}","{\"layers\":[\"hazards\",\"objects\"]}"}.Concat(
            new[]{"map","geometry","objects","hazards","landingRegions","placementCandidates","objectPlacementCandidates"}
                .Select(layer=>"{\"layers\":[\""+layer+"\"]}"));
        foreach(string layers in localCases)
        {
            var current = Node(ServerSceneReader.Read(actor,identity,Args(layers)));
            Check(!current["lighting"]["available"].GetValue<bool>() && current["lighting"]["mean"] == null,
                "dedicated lighting exposes unavailability without a fabricated value");
            current.Remove("lighting");
            Check(JsonNode.DeepEquals(Node(BaselineServerSceneReader.Read(actor,identity,Args(layers))),current),
                "L0 wire compatibility apart from additive lighting status: "+layers);
        }
        var body = new WorldRect(0, 0, 20, 42);
        var still = new StateVector(0, 0);
        var approaching = new WorldRect(50, 10, 8, 8);
        Check(SceneThreat.Project(body, new(2, 0), approaching, new(-3, 0)).ContactAfterTicks == 6,
            "relative velocity yields the first box contact");
        Check(SceneThreat.Project(body, still, approaching, new(-2.5f, 0)).ContactAfterTicks == 12,
            "contact exactly at the horizon is included");
        Check(SceneThreat.Project(body, still, approaching, new(-2.5f, 0), extraUpdates: 1).ContactAfterTicks == 6,
            "projectile substeps preserve game-tick units");
        Check(SceneThreat.Project(body, still, approaching, new(-2, 0)).ContactAfterTicks == null,
            "contact beyond the horizon is omitted");
        Check(SceneThreat.Project(body, still, approaching, new(3, 0)).ContactAfterTicks == null,
            "receding entity does not project contact");
        Check(SceneThreat.Project(body, still, approaching with { Y = 50 }, new(-5, 0)).ContactAfterTicks == null,
            "parallel motion outside the other axis does not project contact");
        Check(SceneThreat.Project(body, still, approaching with { X = 10 }, still).ContactAfterTicks == 0,
            "current overlap is represented at tick zero");
        Check(SceneThreat.Project(body, still, approaching, new(float.NaN, 0)).Status == "unavailable",
            "invalid velocity cannot establish a prediction");
        Main.npc = new[]{new NPC { position = new(376, 270) }};
        Main.projectile = new[]{new Projectile { position = new(410, 270), velocity = new(-5, 0) }};
        var hazardScene = Node(ServerSceneReader.Read(actor,identity,Args("{\"layers\":[\"hazards\",\"geometry\"],\"maxHazards\":1}")));
        var hazard = hazardScene["hazards"]["entities"]["items"][0];
        Check(hazard["kind"].GetValue<string>() == "hostile_projectile" && hazard["threat"]["contactAfterTicks"].GetValue<double>() == 8
            && hazard["observedGameUpdate"].GetValue<ulong>() == Main.GameUpdateCount
            && hazardScene["hazards"]["entities"]["truncated"].GetValue<bool>(), "projected contact ranks first under the existing list cap");
        var next = Node(ServerSceneReader.Read(actor,identity,Args("{\"layers\":[\"hazards\"],\"maxHazards\":1}")));
        Check(next["hazards"]["entities"]["items"][0]["observationRef"].GetValue<string>() != hazard["observationRef"].GetValue<string>(),
            "projectile observation identity cannot survive a new scene");
        Main.npc = Array.Empty<NPC>(); Main.projectile = Array.Empty<Projectile>();
        foreach(string bad in new[]{"{\"width\":120}","{\"scale\":\"region\",\"width\":128,\"height\":96}","{\"scale\":\"region\",\"layers\":[\"landingRegions\"]}","{\"scale\":\"region\",\"maxPlacements\":1}","{\"scale\":true}","{\"scale\":\"region\",\"height\":28}","{\"scale\":\"region\",\"width\":45.5}"})
        {
            bool rejected=false;try{AtomicParameters.Validate("observe_scene",Args(bad),true);}catch(AtomicParameterException){rejected=true;}
            Check(rejected,"invalid regional arguments rejected: "+bad);
        }
        // All shape, knowledge and overlapping-hazard states survive exact coding,
        // including checkerboard input where RLE would be larger than raw indices.
        var cells=new SceneCell[128,64];var shapes=new[]{'.','#','=','h','1','2','3','4','A','?'};
        for(int y=0;y<64;y++)for(int x=0;x<128;x++)
        {
            char c=shapes[(x+y)%shapes.Length];
            cells[x,y]=new(new(c=='?'?"unloaded":"read",HasTile:c!='.',Solid:c=='#',SolidTop:c=='=',HalfBlock:c=='h',Slope:c is >= '1' and <= '4'?c-'0':0,Actuated:c=='A'),c,
                Sources:(x+y)%7==0?new[]{"lava","touch_hot"}:Array.Empty<string>());
        }
        var encoded=JsonSerializer.SerializeToNode(SceneRegion.Encode(cells,c=>SceneGeometry.Geometry(c).ToString()),Json);
        var mask=JsonSerializer.SerializeToNode(SceneRegion.Encode(cells,SceneRegion.HazardMask),Json);
        for(int y=0;y<64;y++)for(int x=0;x<128;x++)
        {Check(At(encoded,x,y).GetValue<string>()==SceneGeometry.Geometry(cells[x,y]).ToString(),"shape not lost");Check(At(mask,x,y).GetValue<int>()==SceneRegion.HazardMask(cells[x,y]),"overlapping danger not lost");}
        for(int y=0;y<64;y++)for(int x=0;x<128;x++)cells[x,y]=cells[x,y] with{Facts=cells[x,y].Facts with{FrameX=y*128+x}};
        var many=JsonSerializer.SerializeToNode(SceneRegion.Encode(cells,c=>c.Facts.FrameX),Json);
        foreach(int index in new[]{0,15,16,255,256,4095,4096,8191})
            Check(At(many,index%128,index/128).GetValue<int>()==index,"hex indices retain multi-digit palette boundaries");
        SceneReaderFixture.RegionalCell=(x,y)=>
        {
            if(x<0||y<0)return new(new("out_of_world"),'?');
            if(x==60&&y==18)return new(new("unloaded"),'?');
            if(x==61&&y==18)return new(new("read",true,999,CollisionKnowledge:"unknown_mod_hooks"),'?');
            if(x==62&&y==18)return new(new("read",true,48,LiquidAmount:255,LiquidType:1,Hot:true),'!',Sources:new[]{"lava","touch_hot"});
            if(x==63&&y==18)return new(new("read",true,7,Solid:true),'#');
            return new(new("read",y>=20,y>=20?1:null,Solid:y>=20),y>=20?'#':'.');
        };
        var args=Args("{\"scale\":\"region\",\"maxObjects\":1,\"maxHazards\":1}");
        // Warm the new serializer/encoder before checking successful full sampling.
        ServerSceneReader.Read(actor,identity,args);
        SceneReaderFixture.ResetCounts();
        var region=JsonSerializer.SerializeToNode(ServerSceneReader.Read(actor,identity,args),Json);
        int ox=region["bounds"]["minX"].GetValue<int>(),oy=region["bounds"]["minY"].GetValue<int>();
        Check(region["schemaVersion"].GetValue<int>()==3,"new explicit region schema");
        Check(region["coverage"]["requestedTiles"].GetValue<int>()==8160,"1920x1080-equivalent 120x68 request");
        Check(region["coverage"]["unknownByReason"]["out_of_world"].GetValue<int>()>0,"world outside is not air");
        Check(At(region["coverage"]["knowledge"],60-ox,18-oy)["tile"].GetValue<string>()=="unloaded","unloaded stays distinct");
        Check(At(region["coverage"]["knowledge"],61-ox,18-oy)["collision"].GetValue<string>()=="unknown_mod_hooks","Mod collision unknown stays distinct");
        Check(At(region["geometry"]["grid"],62-ox,18-oy).GetValue<string>()==".","liquid does not overwrite underlying geometry");
        Check(At(region["hazards"]["tileMask"],62-ox,18-oy).GetValue<int>()==(1<<1|1<<6),"lava/hot preserved beyond hazard details cap");
        Check(region["objects"]["items"].AsArray().Any(o=>o["kind"].GetValue<string>()=="ore_component")
            &&region["objects"]["items"].AsArray().All(o=>o["target"]==null),"resource beyond L0 is knowledge, not an unvalidated action target");
        Check(SceneReaderFixture.Worlds==0&&SceneReaderFixture.Definitions==0,"L1 never constructs native candidate worlds");
        Check(region["includedLayers"].AsArray().Select(n=>n.GetValue<string>()).SequenceEqual(new[]{"geometry","objects","hazards"}),"bounded default L1 layers");
        File.WriteAllText(Path.Combine(output,"region.json"),region.ToJsonString(Json));
        Main.npc=new[]{new NPC{position=new(62*16,18*16)}};
        Main.projectile=new[]{new Projectile{position=new(62*16,18*16)}};
        var overlay=JsonSerializer.SerializeToNode(ServerSceneReader.Read(actor,identity,Args("{\"scale\":\"region\",\"layers\":[\"map\",\"geometry\",\"hazards\"]}")),Json);
        Check(JsonNode.DeepEquals(overlay["map"]["terrain"],overlay["geometry"]["grid"]),"entity display cannot overwrite source geometry");
        Check(overlay["map"]["markers"]["returnedCount"].GetValue<int>()==2,"current NPC/projectile markers are a separate collection");
        Check(At(overlay["hazards"]["tileMask"],62-ox,18-oy).GetValue<int>()==(1<<1|1<<6)
            &&overlay["hazards"]["entities"]["returnedCount"].GetValue<int>()==2,"dynamic and tile hazards coexist without masking");
        Main.npc=Array.Empty<NPC>();Main.projectile=Array.Empty<Projectile>();
        // Force a single expensive read; the next bounded sampler checkpoint must
        // leave all untouched cells explicitly not_sampled, with no invented safety.
        bool slow=true;var previous=SceneReaderFixture.RegionalCell;
        SceneReaderFixture.RegionalCell=(x,y)=>{if(slow){slow=false;System.Threading.Thread.Sleep(20);}return previous(x,y);};
        var partial=JsonSerializer.SerializeToNode(ServerSceneReader.Read(actor,identity,args),Json);
        Check(partial["coverage"]["samplingTruncated"].GetValue<bool>()&&!partial["coverage"]["geometryComplete"].GetValue<bool>(),"sampling budget has explicit partial semantics");
        Check(partial["coverage"]["scannedTiles"].GetValue<int>()==128,"deadline checked each bounded 128-tile chunk");
        Check(At(partial["geometry"]["grid"],119,67).GetValue<string>()=="?"&&At(partial["coverage"]["knowledge"],119,67)["tile"].GetValue<string>()=="not_sampled","budget remainder cannot become empty space");
        File.WriteAllText(Path.Combine(output,"partial.json"),partial.ToJsonString(Json));
        var costs=new List<object>();
        foreach(string pattern in new[]{"flat","checkerboard","fragmented_ores"})
        {
            SceneReaderFixture.RegionalCell=(x,y)=>
            {
                if(pattern=="flat")return new(new("read",y>=20,y>=20?1:null,Solid:y>=20),y>=20?'#':'.');
                bool solid=((x+y)&1)==0;
                int type=pattern=="fragmented_ores"&&solid?7:1;
                return new(new("read",solid,solid?type:null,Solid:solid,LiquidType:1,LiquidAmount:solid?0:255),solid?'#':'!');
            };
            for(int i=0;i<3;i++)
            {
                var dense=JsonSerializer.SerializeToNode(ServerSceneReader.Read(actor,identity,Args("{\"scale\":\"region\",\"width\":128,\"height\":64,\"maxObjects\":1,\"maxHazards\":1}")),Json);
                int bytes=System.Text.Encoding.UTF8.GetByteCount(dense.ToJsonString(Json));
                Check(bytes<196608,"bounded worst-pattern payload");
                int Nodes(JsonNode node)=>1+(node is JsonObject obj?obj.Sum(p=>1+Nodes(p.Value)):
                    node is JsonArray array?array.Sum(Nodes):0);
                Check(Nodes(dense)<16000,"dense regional data fits unchanged Worker 20000-node envelope budget");
                Check(dense["coverage"]["scannedTiles"].GetValue<int>()<=8192,"bounded worst-pattern sampling");
                if(pattern=="checkerboard"&&dense["hazards"]["tiles"]["truncated"].GetValue<bool>())
                    Check(dense["hazards"]["tileMask"]["palette"].AsArray().Any(v=>v.GetValue<int>()!=0),"truncation cannot erase checkerboard hazards");
                if(pattern=="fragmented_ores"&&dense["coverage"]["scannedTiles"].GetValue<int>()>512)
                {
                    Check(dense["objects"]["groupingTruncated"].GetValue<bool>()&&dense["objects"]["matchedCount"]==null
                        &&dense["objects"]["omittedCount"]==null,"group budget never publishes an invented exact total");
                    Check(dense["objects"]["kindGrid"]["palette"].AsArray().Any(v=>v["kind"].GetValue<string>()=="ore_component"),"group truncation retains resource facts separately");
                }
                costs.Add(new{pattern,iteration=i,bytes,sample=dense["sample"].DeepClone(),coverage=dense["coverage"]["samplingTruncated"].DeepClone()});
                if(pattern=="fragmented_ores")File.WriteAllText(Path.Combine(output,"dense.json"),dense.ToJsonString(Json));
            }
        }
        File.WriteAllText(Path.Combine(output,"costs.json"),JsonSerializer.Serialize(costs,Json));
        SceneReaderFixture.RegionalCell=null;
        Console.WriteLine($"M4.5 region: {checks} checks; actual reader / d8e98e1 compatibility, detached engine facts; not native acceptance.");
    }
}
