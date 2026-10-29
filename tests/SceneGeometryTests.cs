using System;
using System.Linq;
using System.Text.Json;
using CompanionBridge;
using CompanionBridge.Motion;

public static class SceneGeometryTests
{
    private static int checks;
    private static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
    private static SceneCell Cell(char c)=>new(new("read",c!='.',c=='.'?null:1,Solid:c=='#',SolidTop:c=='=',HalfBlock:c=='h',Slope:c is >= '1' and <= '4'?c-'0':0),c);
    private static SceneCell[,] Grid()
    {
        var grid=new SceneCell[20,18];for(int x=0;x<20;x++)for(int y=0;y<18;y++)grid[x,y]=Cell(y>=14?'#':'.');return grid;
    }
    public static void Run()
    {
        var grid=Grid();
        for(int x=5;x<12;x++)grid[x,9]=Cell('=');
        var regions=SceneGeometry.Landings(grid,100,200,1640,3424);
        Check(regions.Any(r=>r.Target.SurfaceY==209*16)&&regions.Any(r=>r.Target.SurfaceY==214*16),"side-view map retains stacked platform and lower cave/ground surfaces");
        char Read(int x,int y)=>x<100||x>=120||y<200||y>=218?'?':grid[x-100,y-200].Navigation;
        foreach(var r in regions)for(float x=r.Target.MinPlayerLeftX;x<=r.Target.MaxPlayerLeftX;x+=.25f)
            Check(r.Target.Arrived(new(x,r.Target.SurfaceY-42,0,0,true),new MapWorld(Read)),"region must share actual LandingRegion/MotionSupport acceptance, including fractional x");
        Check(!regions.Any(r=>r.Target.MinPlayerLeftX<=r.Target.MaxPlayerLeftX&&r.Target.SurfaceY==210*16),"empty air row is not a surface");
        var blocked=Grid();for(int x=0;x<20;x++)blocked[x,12]=Cell('#');
        Check(!SceneGeometry.Landings(blocked,0,0,0,224).Any(r=>r.Target.SurfaceY==224),"low ceiling rejects insufficient 42px body clearance");
        foreach(char shape in new[]{'h','1','2','a','b','?','!'})
        {
            var g=Grid();for(int x=0;x<20;x++)g[x,14]=Cell(shape);
            Check(!SceneGeometry.Landings(g,0,0,0,224).Any(r=>r.Target.SurfaceY==224),"unsupported flat final surface rejected: "+shape);
        }
        Check(SceneGeometry.Landings(Grid(),0,0,0,224,_=>false).Length==0,"native validator may reject geometry candidate; cannot be overridden");
        var unknown=Grid();unknown[8,12]=new(new("unloaded"),'?');
        var unknownRegions=SceneGeometry.Landings(unknown,0,0,0,224);
        Check(unknownRegions.All(r=>r.Target.MaxPlayerLeftX+20<=8*16||r.Target.MinPlayerLeftX>=9*16),"unknown body cell splits continuous region");
        var trees=Grid();
        SceneCell Object(string kind,int type)=>new(new("read",true,type),'.',kind,"Terraria/test");
        trees[4,10]=Object("tree_component",5);trees[4,11]=Object("tree_component",5);trees[5,10]=Object("tree_component",5);
        trees[8,10]=Object("tree_component",5);
        trees[10,14]=Object("ore_component",7);trees[11,14]=Object("ore_component",7);trees[0,15]=Object("ore_component",7);
        var objects=SceneGeometry.Objects(trees,50,60);
        Check(objects.Count(o=>o.Kind=="tree_component")==2&&objects.Where(o=>o.Kind=="tree_component").All(o=>!o.Complete),"connected branches merge but disconnected trunks remain explicitly partial, never whole-tree count");
        Check(objects.Single(o=>o.Kind=="tree_component"&&o.ObservedTileCount==3).ObservedBounds==new TileBounds(54,70,2,2),"component exact observed bounds retained");
        Check(objects.Single(o=>o.Kind=="ore_component"&&o.ObservedTileCount==2).Complete,"bounded observed same-type vein can be complete");
        Check(!objects.Single(o=>o.Kind=="ore_component"&&o.ObservedTileCount==1).Complete,"edge vein cannot claim complete");
        trees[15,11]=new(new("read",true,18),'.',"workbench","Terraria/WorkBenches",65,71);
        trees[16,11]=new(new("read",true,18),'.',"workbench","Terraria/WorkBenches",65,71);
        Check(SceneGeometry.Objects(trees,50,60).Count(o=>o.Kind=="workbench")==1,"multi-cell object groups by existing registered anchor");
        var hazard=Grid();hazard[3,13]=new(new("read",LiquidType:1,LiquidAmount:100),'!',Sources:new[]{"lava","touch_hot"});
        hazard[4,13]=new(new("read",LiquidType:1,LiquidAmount:255),'!',Sources:new[]{"lava","touch_hot"});
        var hazards=SceneGeometry.Hazards(hazard,0,0);
        Check(hazards.Length==1&&hazards[0].MinX==3&&hazards[0].MaxX==4&&hazards[0].MinLiquidAmount==100&&hazards[0].MaxLiquidAmount==255,"hazard runs retain overlapping sources and liquid amount range");
        Check(SceneGeometry.Geometry(hazard[3,13])=='.'&&SceneGeometry.Danger(hazard[3,13].Sources)=='L',"liquid hazard never destroys independent geometry fact");
        Check(SceneGeometry.Danger(new[]{"water"})=='~'&&SceneGeometry.Danger(new[]{"honey"})=='H'&&SceneGeometry.Danger(new[]{"shimmer"})=='S',"liquid types remain distinct");
        hazard[4,13]=new(new("read",true,48,TouchDamage:20),'!',ContentKey:"Terraria/Spikes",Sources:new[]{"lava","touch_hot"});
        Check(SceneGeometry.Hazards(hazard,0,0).Length==2&&SceneGeometry.Hazards(hazard,0,0)[1].TouchDamage==20,"different native tile sources remain distinct despite identical source flags");
        Check(SceneGeometry.Geometry(new(new("read"),'?'))=='?',"navigation unknown world border cannot be presented as known empty geometry");
        trees[9,14]=new(new("read"),'?');
        Check(!SceneGeometry.Objects(trees,50,60).Single(o=>o.Kind=="ore_component"&&o.ObservedTileCount==2).Complete,"navigation unknown neighbour makes component coverage partial");
        var actor=new WorldRect(9*16,14*16-42,20,42);
        var sites=SceneGeometry.Placements(Grid(),0,0,actor,new[]{actor});
        Check(sites.Length>0,"batch placement candidates exist near empty solid attachment");
        bool At(ScenePlacement site,int x,int y){var point=JsonSerializer.SerializeToElement(site.Target);return point.GetProperty("x").GetInt32()==x&&point.GetProperty("y").GetInt32()==y;}
        Check(sites.Any(s=>At(s,8,13))&&!sites.Any(s=>At(s,9,13)),"near attachment appears; actor-overlapping cell does not");
        var placementGrid=Grid();placementGrid[8,13]=new(new("read",LiquidAmount:1),'!');
        Check(!SceneGeometry.Placements(placementGrid,0,0,actor,new[]{actor}).Any(s=>At(s,8,13)),"even shallow liquid removes placement candidate");
        placementGrid[8,13]=new(new("read"),'?');
        Check(!SceneGeometry.Placements(placementGrid,0,0,actor,new[]{actor}).Any(s=>At(s,8,13)),"navigation-unknown empty cell cannot become a placement candidate");
        Check(!SceneGeometry.Placements(Grid(),0,0,actor,new[]{actor,new WorldRect(128,208,16,16)}).Any(s=>At(s,8,13)),"other observed player also excludes candidate");
        Check(sites.All(s=>s.Validation.Contains("native rules not evaluated")),"candidate does not promise native permission");
        SceneObjectPlacement[] Benches(SceneCell[,] g,WorldRect[] bodies=null)=>SceneGeometry.ObjectPlacements(g,0,0,actor,bodies??new[]{actor},2,1,0,0,0,2);
        bool BenchAt(SceneObjectPlacement b,int x)=>JsonSerializer.SerializeToElement(b.Target).GetProperty("x").GetInt32()==x&&b.Footprint.Y==13;
        var benches=Benches(Grid());
        Check(benches.Any(b=>BenchAt(b,7))&&!benches.Any(b=>BenchAt(b,8)),"whole bench excludes actor, anchor and footprint are distinct from ordinary tile sites");
        var bench=benches.First(b=>BenchAt(b,7));
        Check(bench.Footprint==new TileBounds(7,13,2,1)&&bench.Support==new TileBounds(7,14,2,1)&&bench.DistancePixels<=80,"full workbench footprint/support retained");
        foreach(string reason in new[]{"blocked","liquid","unknown","support","slope","supportLiquid"})
        {
            var g=Grid();
            if(reason=="blocked")g[8,13]=Cell('#');
            if(reason=="liquid")g[8,13]=new(new("read",LiquidAmount:1),'.');
            if(reason=="unknown")g[8,13]=new(new("unloaded"),'?');
            if(reason=="support")g[8,14]=Cell('.');
            if(reason=="slope")g[8,14]=Cell('1');
            if(reason=="supportLiquid")g[8,14]=new(new("read",true,1,Solid:true,LiquidAmount:1),'#');
            Check(!Benches(g).Any(b=>BenchAt(b,7)),"second footprint/support cell rejects "+reason);
        }
        Check(!Benches(Grid(),new[]{actor,new WorldRect(128,208,16,16)}).Any(b=>BenchAt(b,7)),"second workbench cell excludes other player");
        Check(benches.All(b=>b.DistancePixels<=80&&b.Validation.Contains("native rules not evaluated")),"bench candidates are conservative geometric observations");
        Check(SceneGeometry.Limit(benches,0).Truncated,"object placement truncation remains explicit");
        Check(SceneGeometry.Limit(objects,1) is {Truncated:true,ReturnedCount:1} limited&&limited.OmittedCount==objects.Length-1,"collection truncation cannot masquerade as absence");
        ObjectPlacementDiagnostics();
        Console.WriteLine($"Scene geometry: {checks} checks, shared support/region rules; synthetic grid, not native/LLM acceptance.");
    }
    private static void ObjectPlacementDiagnostics()
    {
        SceneCell[,] Flat(int width=2)
        {
            var g=new SceneCell[width,2];
            for(int x=0;x<width;x++){g[x,0]=Cell('.');g[x,1]=Cell('#');}return g;
        }
        SceneObjectPlacementList Inspect(SceneCell[,] g,WorldRect actor=null,WorldRect[] bodies=null,int cap=12,int ox=0,int oy=0,int width=2)
        {
            actor??=new WorldRect(ox*16,oy*16-32,16,16);
            var all=SceneGeometry.ObjectPlacements(g,ox,oy,actor,bodies??new[]{actor},width,1,0,0,0,2,out var d);
            var result=SceneGeometry.LimitObjectPlacements(all,cap,d);
            Check(d.AnchorCount==d.InBoundsAnchorCount+d.NotCheckedCount,"anchor domain partitions covered and unexamined positions");
            Check(d.InBoundsAnchorCount==d.CheckedCount+d.UnknownCount,"unknown anchors never count as fully checked");
            Check(d.CheckedCount==d.RejectedCount+result.MatchedCount,"checked anchors partition accepted and rejected");
            Check(d.RejectedByReason.Values.Sum()==d.RejectedCount,"first-failure categories are mutually exclusive");
            Check(d.NotCheckedByReason.Values.Sum()==d.NotCheckedCount,"unexamined counts are independent of confirmed rejections");
            Check(result.OmittedCount==result.MatchedCount-result.ReturnedCount&&result.Truncated==(result.OmittedCount>0),"omission only means accepted items beyond cap");
            Check(d.FirstExamples.Count<=7,"examples are at most one per fixed reason, not per rejected anchor");
            return result;
        }
        void Only(SceneObjectPlacementList r,string reason)
        {
            Check(r.MatchedCount==0&&r.Diagnostics.RejectedCount==1&&r.Diagnostics.CheckedCount==1,"single in-bounds anchor is confirmed rejected: "+reason);
            Check(r.Diagnostics.RejectedByReason[reason]==1&&r.Diagnostics.CoveredScopeComplete,"all covered anchors fully checked: "+reason);
            Check(!r.Truncated&&r.OmittedCount==0,"empty rejection list is not truncation: "+reason);
        }
        var collision=Inspect(Flat(),bodies:new[]{new WorldRect(16,0,16,16)});
        Only(collision,"player_collision");
        Check(collision.Diagnostics.FirstExamples["player_collision"].Footprint==new TileBounds(0,0,2,1),"collision uses whole object, including second cell");
        var missing=Flat();missing[1,1]=Cell('.');Only(Inspect(missing),"missing_support");
        var far=Inspect(Flat(),actor:new WorldRect(1000,-32,16,16));Only(far,"out_of_reach");
        var distance=JsonSerializer.SerializeToElement(far.Diagnostics.FirstExamples["out_of_reach"].Detail);
        Check(distance.GetProperty("distancePixels").GetSingle()>80&&distance.GetProperty("limitPixels").GetInt32()==80,"reach detail is the actual anchor distance and unchanged limit");
        var occupied=Flat();occupied[1,0]=Cell('#');
        var occupiedResult=Inspect(occupied,ox:100,oy:200);Only(occupiedResult,"footprint_occupied");
        var blocked=occupiedResult.Diagnostics.FirstExamples["footprint_occupied"];
        var detail=JsonSerializer.SerializeToElement(blocked.Detail);
        Check(blocked.Footprint==new TileBounds(100,200,2,1)&&detail.GetProperty("x").GetInt32()==101&&detail.GetProperty("y").GetInt32()==200&&detail.GetProperty("tileType").GetInt32()==1,"occupied example reports actual world tile and type, including second cell");
        foreach(bool support in new[]{false,true})
        {
            var wet=Flat();var original=wet[1,support?1:0];wet[1,support?1:0]=original with{Facts=original.Facts with{LiquidAmount=1}};
            Only(Inspect(wet),"liquid_present");
        }
        foreach(var unknown in new[]{new SceneCell(new("unloaded"),'?'),new(new("out_of_world"),'?'),
            new(new("read",CollisionKnowledge:"uninterpreted"),'.'),new(new("read"),'?')})
        foreach(bool support in new[]{false,true})
        {
            var g=Flat();g[1,support?1:0]=unknown;
            // A different known obstruction must not turn partial knowledge into a full rejection.
            g[0,0]=Cell('#');var r=Inspect(g);
            Check(r.Diagnostics.UnknownCount==1&&r.Diagnostics.CheckedCount==0&&r.Diagnostics.RejectedCount==0&&!r.Diagnostics.CoveredScopeComplete,"unknown/unloaded footprint or support is incomplete, not confirmed occupied/support failure");
            Check(r.MatchedCount==0&&!r.Truncated&&r.OmittedCount==0&&r.Diagnostics.FirstExamples.ContainsKey("unknown"),"empty partial inspection retains unknown evidence without omission");
        }
        var shared=Flat(5);shared[2,0]=new(new("unloaded"),'?');var sharedResult=Inspect(shared);
        Check(sharedResult.Diagnostics.UnknownCount==2&&sharedResult.MatchedCount==2,"one unknown tile affects two distinct overlapping anchors, each counted once");
        var manyUnknown=Flat();manyUnknown[0,0]=manyUnknown[1,0]=manyUnknown[1,1]=new(new("unloaded"),'?');
        Check(Inspect(manyUnknown).Diagnostics.UnknownCount==1,"several unknown footprint/support tiles still count as only one unknown anchor");
        var overlap=Flat();overlap[0,0]=Cell('#');overlap[1,0]=new(new("read",LiquidAmount:1),'.');overlap[0,1]=Cell('.');
        var multiple=Inspect(overlap,actor:new WorldRect(1000,-32,16,16),bodies:new[]{new WorldRect(0,0,32,16)});
        Only(multiple,"footprint_occupied");
        Check(multiple.Diagnostics.RejectedByReason.Where(p=>p.Key!="footprint_occupied").All(p=>p.Value==0),"one anchor violating several rules counts only first reason");
        var mixed=Flat(11);mixed[1,0]=Cell('#');mixed[3,0]=new(new("read",LiquidAmount:1),'.');mixed[5,1]=Cell('.');
        var mix=Inspect(mixed).Diagnostics;
        Check(mix.RejectedCount==10&&mix.RejectedByReason["footprint_occupied"]==2&&mix.RejectedByReason["liquid_present"]==2&&mix.RejectedByReason["missing_support"]==2&&mix.RejectedByReason["out_of_reach"]==4,"mixed anchors have exact, disjoint rejection counts");
        var normal=Inspect(Flat(5),cap:20);
        Check(normal.Items.Length==4&&normal.Diagnostics.RejectedCount==0&&normal.Diagnostics.UnknownCount==0&&normal.Diagnostics.CoveredScopeComplete,"all four existing in-reach dry supported anchors survive unchanged");
        Check(normal.Items.Select(p=>p.Footprint.X).SequenceEqual(new[]{0,1,2,3})&&normal.Items.All(p=>p.Footprint.Width==2&&p.Support.Width==2),"candidate order and full footprint/support are preserved");
        var capped=SceneGeometry.LimitObjectPlacements(normal.Items,1,normal.Diagnostics);
        Check(capped.MatchedCount==4&&capped.ReturnedCount==1&&capped.OmittedCount==3&&capped.Diagnostics==normal.Diagnostics,"list truncation does not alter screening statistics or matchedCount");
        var zero=SceneGeometry.LimitObjectPlacements(normal.Items,0,normal.Diagnostics);
        Check(zero.Items.Length==0&&zero.Truncated&&zero.MatchedCount==4&&zero.OmittedCount==4,"empty truncated list still declares accepted candidates");
        Check(normal.Diagnostics.NotCheckedCount==6&&normal.Diagnostics.NotCheckedByReason["outside_observation"]==6,"map edge anchors with uncovered full footprint/support are not tested or rejected");
        var small=new SceneCell[2,1];small[0,0]=small[1,0]=Cell('.');
        var edges=Inspect(small);
        Check(edges.Diagnostics.NotCheckedCount==2&&edges.Diagnostics.RejectedCount==0&&!edges.Diagnostics.CoveredScopeComplete,"no covered anchor means inspection incomplete, not no possible placement");
        var unavailable=Inspect(Flat(),width:0);
        Check(unavailable.Diagnostics.NotCheckedByReason["definition_unavailable"]==4&&unavailable.Diagnostics.CheckedCount==0&&!unavailable.Diagnostics.CoveredScopeComplete,"unavailable native definition never invents checked candidates");
        var json=JsonSerializer.SerializeToElement(capped,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        Check(json.GetProperty("matchedCount").GetInt32()==4&&json.GetProperty("diagnostics").GetProperty("rejectedCount").GetInt32()==0&&json.GetProperty("items").GetArrayLength()==1,"wire shape preserves collection fields and only adds diagnostics");
    }
    private sealed class MapWorld:IMotionWorld
    {
        private readonly Func<int,int,char> read;internal MapWorld(Func<int,int,char> read){this.read=read;}
        public bool SafeLanding(PhysicsState s)=>MotionSupport.SafeLanding(s,read);
        public bool SafeFlatLanding(PhysicsState s)=>MotionSupport.SafeFlatLanding(s,read);
        public string LandingRejection(PhysicsState s)=>MotionSupport.LandingRejection(s,read);
        public bool SafeSweep(PhysicsState a,PhysicsState b,out string reason){reason=null;return true;}
        public PhysicsState Move(PhysicsState s,float x,float y,float gravity)=>throw new NotSupportedException();
    }
}
