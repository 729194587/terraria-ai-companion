using System;
using System.Collections.Generic;
using System.Linq;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;

namespace CompanionBridge.ServerIpc;

// Failure scope and attempt accounting are independent of the rolling route planner.
internal sealed class TerrainRouteMemory
{
    private (long Terrain,int Capability,PhysicsParameters Physics,int Pending)? scope;
    private readonly List<(LandingRegion Region,ExcavationCell[] Batch)> attempts=new();
    private readonly List<LandingRegion> failedOpeningMoves=new();
    private sealed record FailedMove(LandingRegion Landing,PhysicsParameters Physics,long Equipment,
        Dictionary<(int X,int Y),TerrainCell> Cells);
    private readonly List<FailedMove> destinationMoves=new();

    internal void RejectDestinationMove(LandingRegion landing,PhysicsParameters physics,long equipment,
        Dictionary<(int X,int Y),TerrainCell> dependencies)
    {
        if(!OpeningMoveLimited(landing))destinationMoves.Add(new(landing,physics,equipment,new(dependencies)));
    }

    // These keys belong to the failed trajectory, independent of the actor's current window.
    internal bool ObserveDestination(PhysicsParameters physics,long equipment)=>
        destinationMoves.RemoveAll(f=>f.Physics!=physics||f.Equipment!=equipment||
            f.Cells.Any(c=>TerrainReader.ReadEnvironment(c.Key.X,c.Key.Y)!=c.Value))>0;
    internal bool HasDestinationFailures=>destinationMoves.Count!=0;
    internal int Attempts=>attempts.Count;

    internal bool OpeningMoveLimited(LandingRegion region)=>TravelExhausted||failedOpeningMoves.Count+destinationMoves.Count>=16
        ||destinationMoves.Any(f=>SameLanding(f.Landing,region))
        ||failedOpeningMoves.Any(prior=>SameLanding(prior,region));
    internal void RejectOpeningMove(LandingRegion region)
    {
        if(!OpeningMoveLimited(region))failedOpeningMoves.Add(region);
    }

    // Charge actual search and child updates, including unsuccessful opening attempts.
    // A scope invalidation restores candidates but never refunds spent updates.
    internal ulong WorkUpdates { get; private set; }
    internal ulong TravelUpdates { get; private set; }
    internal bool WorkExhausted=>WorkUpdates>=1800;
    internal bool TravelExhausted=>TravelUpdates>=900;
    internal void Charge(ulong elapsed,bool travel)
    {
        WorkUpdates+=elapsed;
        if(travel)TravelUpdates+=elapsed;
    }
    internal void Mined(){WorkUpdates=TravelUpdates=0;}

    internal bool Observe((int,int,int,int) work,(int,int,int,int) departure,int capability,PhysicsParameters physics,int pending)
    {
        long terrain=TerrainSignature(work,TerrainSignature(departure,1469598103934665603));
        var observed=(terrain,capability,physics,pending);
        if(scope==observed)return false;
        scope=observed;
        attempts.Clear();failedOpeningMoves.Clear();
        return true;
    }

    internal void Attempt(LandingRegion region,ExcavationCell[] batch)=>attempts.Add((region,batch));

    // Adjacent half-tile samples overlap most of the player's 20px body. Keep
    // alternative floors and samples offering a different first batch eligible.
    internal bool Tried(LandingRegion region,ExcavationCell[] batch)=>attempts.Any(a=>
        SameLanding(a.Region,region)&&a.Batch.SequenceEqual(batch));

    private static bool SameLanding(LandingRegion prior,LandingRegion region)=>
        Math.Abs(prior.SurfaceY-region.SurfaceY)<=LandingRegion.FeetTolerance
        &&Math.Abs(prior.MinPlayerLeftX-region.MinPlayerLeftX)<=8
        &&Math.Abs(prior.MaxPlayerLeftX-region.MaxPlayerLeftX)<=8;

    private static long TerrainSignature((int Left,int Right,int Top,int Bottom) bounds,long hash)
    {
        for(int x=bounds.Left;x<=bounds.Right;x++)
        for(int y=bounds.Top;y<=bounds.Bottom;y++)
        {
            var cell=TerrainReader.ReadEnvironment(x,y);
            long stamp=(long)(cell.TileType.GetValueOrDefault(-1)+1)<<32 | (long)cell.Shape<<16 | (uint)cell.Liquid.Stamp;
            hash=unchecked((hash^stamp^(cell.Actuated?1L<<56:0))*1099511628211);
        }
        return hash;
    }
}
