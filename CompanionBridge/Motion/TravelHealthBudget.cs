using System;

namespace CompanionBridge.Motion;

internal sealed class TravelHealthBudget
{
    private (int Session,int Generation)? scope;
    internal int StartLife {get;private set;}

    internal void Begin(int session,int generation,int life)
    {
        if(scope==(session,generation))return;
        scope=(session,generation);
        StartLife=life;
    }

    internal void Reset()=>scope=null;

    // Keep one third of starting life as the spending limit and reserve
    // one quarter of maximum life, including when starting wounded.
    internal static bool Exceeded(int startLife,int remaining,int maximum)
        =>remaining<startLife && remaining<=Math.Max(maximum/4,startLife-startLife/3);
}
