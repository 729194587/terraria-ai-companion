using System;
using CompanionBridge.Motion;

public static class GrappleClimbTests
{
    public static void Run()
    {
        const int shaft=100,floor=100,shelf=90,anchor=81;
        var terrain=new SurfaceGraphTests.Terrain{Floor=true};
        var start=new PhysicsState(shaft*16-2,floor*16-42,0,0,true);
        var goal=LandingRegion.AtDestination((shaft+5)*16,shelf*16);
        for(int x=shaft+2;x<=shaft+12;x++)terrain.Tiles[(x,shelf)]='#';
        terrain.Tiles[(shaft,anchor)]='#';
        int checks=0;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
        GrappleClimbPlan Find(PhysicsState s,LandingRegion r,PhysicsParameters p)
            =>GrappleClimbPlan.Find(s,r,p,terrain,terrain);
        var ph=PhysicsParameters.Basic;
        var plan=Find(start,goal,ph);
        Check(plan!=null&&plan.AnchorY==anchor&&plan.Direction==1&&plan.Unchanged(terrain),"separate clear shaft and shelf");
        Check(Find(start with{Vx=.4f},goal,ph)==null,"rolling player cannot launch");
        Check(Find(start,goal,ph with{ExtraJump=ExtraJumpModel.Cloud})!=null,"equipped Cloud permits grounded hook use");
        Check(Find(start,LandingRegion.AtDestination((shaft+5)*16,(shelf-2)*16),ph)==null,"no unsupported target");
        Check(plan.Tail(start,ph,terrain)==null,"ground contact cannot masquerade as a rising detach");
        foreach(char blocked in new[]{'#','?','!','~','='})
        {
            terrain.Tiles[(shaft,anchor+3)]=blocked;
            Check(!plan.Unchanged(terrain)&&!GrappleClimbPlan.ClearShot(start,shaft,anchor,terrain,null),"blocked first hit "+blocked);
        }
        terrain.Tiles.Remove((shaft,anchor+3));
        terrain.Tiles.Remove((shaft,anchor));
        terrain.Tiles[(shaft+1,anchor)]='#';
        var diagonal=Find(start,goal,ph);
        Check(diagonal!=null&&diagonal.AnchorX==shaft+1&&diagonal.Approach==null,"diagonal anchor from actual launch");
        terrain.Tiles.Remove((shaft+1,anchor));
        terrain.Tiles[(shaft,anchor-1)]='#';
        Check(Find(start,goal,ph)==null,"no supported anchor in range");
        terrain.Tiles.Remove((shaft,anchor-1));
        terrain.Tiles[(shaft,anchor)]='#';
        Check(Find(start with{Px=start.Px-96},goal,ph)?.Approach!=null,"run to a settled launch before firing");
        terrain.Tiles[(shaft+4,shelf)]='=';
        Check(Find(start,goal,ph)==null,"platform edge excluded from this MVP");
        Console.WriteLine($"Phase 4.5 grapple geometry: {checks} checks");
    }
}
