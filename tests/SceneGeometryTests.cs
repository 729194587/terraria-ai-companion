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
        Check(SceneGeometry.Limit(objects,1) is {Truncated:true,ReturnedCount:1} limited&&limited.OmittedCount==objects.Length-1,"collection truncation cannot masquerade as absence");
        Console.WriteLine($"Scene geometry: {checks} checks, shared support/region rules; synthetic grid, not native/LLM acceptance.");
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
