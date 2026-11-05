using System;

namespace CompanionBridge.Motion;

internal enum NavigationActivity { Search, Movement, Recovery, VerifiedCheckpoint, VerifiedEffect }

internal sealed class NavigationProgressWatchdog
{
    internal const int ActiveUpdateLimit=180;
    internal int SearchUpdates { get; private set; }
    internal int MovingUpdates { get; private set; }
    internal int NoProgressUpdates { get; private set; }
    internal int VerifiedEffects { get; private set; }
    internal int ActualMovingUpdates { get; private set; }
    internal double ActualTravelPixels { get; private set; }
    internal NavigationActivity Activity { get; private set; }
    internal bool Stalled=>NoProgressUpdates>=ActiveUpdateLimit;
    internal void Reset()
    {SearchUpdates=MovingUpdates=NoProgressUpdates=VerifiedEffects=ActualMovingUpdates=0;ActualTravelPixels=0;}
    internal void MotionObserved(float beforeX,float beforeY,float afterX,float afterY)
    {
        double distance=Math.Sqrt(Math.Pow(afterX-beforeX,2)+Math.Pow(afterY-beforeY,2));
        if(distance>.01){ActualMovingUpdates++;ActualTravelPixels+=distance;}
    }
    internal void Observe(NavigationActivity activity)
    {
        Activity=activity;
        if(activity==NavigationActivity.Search)SearchUpdates++;
        if(activity==NavigationActivity.Movement){MovingUpdates++;NoProgressUpdates++;}
        if(activity is NavigationActivity.VerifiedCheckpoint or NavigationActivity.VerifiedEffect)NoProgressUpdates=0;
        if(activity==NavigationActivity.VerifiedEffect)VerifiedEffects++;
    }
}
