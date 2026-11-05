using System;
using System.Collections.Generic;

namespace CompanionBridge.Motion;

// Navigation-local progress, never a second M3 action/runtime or a world map.
internal sealed class NavigationTripProgress
{
    internal const int UpdateLimit=1800, SearchLimit=128;
    internal const double PlanningLimitMilliseconds=SearchLimit*25;
    internal const int DetourCheckpointLimit=6;
    private readonly Dictionary<PhysicsState,int> visits=new();
    private readonly HashSet<(int X,int Y)> discovered=new();
    private PhysicsState anchor;
    private float bestDistance;
    private int detours;
    internal int DetourCheckpoints {get;private set;}
    internal int RepeatRejections {get;private set;}
    internal int VisitedCheckpoints=>discovered.Count;
    private static (int,int) Key(PhysicsState s)=>((int)MathF.Floor(s.Px/8),(int)MathF.Floor(s.Py/8));
    private static PhysicsState RepeatKey(PhysicsState s)=>s;
    internal void Begin(PhysicsState start,float distance)
    {
        visits.Clear(); discovered.Clear(); visits[RepeatKey(start)]=1; discovered.Add(Key(start));anchor=start;bestDistance=distance;
        detours=DetourCheckpoints=RepeatRejections=0;
    }
    internal bool Detour(PhysicsState s)=>detours<DetourCheckpointLimit
        &&Math.Abs(s.Px-anchor.Px)<=128&&Math.Abs(s.Py-anchor.Py)<=128&&!discovered.Contains(Key(s));
    internal bool Reject(PhysicsState s)
    {
        if(visits.GetValueOrDefault(RepeatKey(s))<2)return false;
        RepeatRejections++;return true;
    }
    internal bool Checkpoint(PhysicsState s,float distance,bool continuous=false)
    {
        bool novel=!discovered.Contains(Key(s));
        bool excursion=Detour(s);
        discovered.Add(Key(s));
        visits[RepeatKey(s)]=visits.GetValueOrDefault(RepeatKey(s))+1;
        if(distance<=bestDistance-4)
        {bestDistance=distance;anchor=s;detours=0;return true;}
        if(continuous&&novel)
        {anchor=s;detours=0;DetourCheckpoints++;return true;}
        if(novel&&excursion){detours++;DetourCheckpoints++;return true;}
        return false;
    }
}
