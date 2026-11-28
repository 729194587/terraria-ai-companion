namespace CompanionBridge.Motion;

internal sealed class TravelHealth
{
    private (int Session,int Generation)? scope;
    private int previousLife;

    internal void Begin(int session,int generation,int life)
    {
        if(scope==(session,generation))return;
        scope=(session,generation);
        previousLife=life;
    }

    internal void Reset()=>scope=null;

    internal bool Observe(int life,int maximum)
    {
        bool stop=LowAfterDamage(previousLife,life,maximum);
        previousLife=life;
        return stop;
    }

    // A wounded player can still start walking to safety. Only a fresh loss
    // at or below the low-health line interrupts travel.
    internal static bool LowAfterDamage(int previous,int remaining,int maximum)
        =>remaining<previous && IsLow(remaining,maximum);

    internal static bool IsLow(int life,int maximum)=>life<=System.Math.Max(25,maximum/4);
}
