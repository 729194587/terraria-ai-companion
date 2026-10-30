using System;
using System.Collections.Generic;

namespace CompanionBridge.Motion;

// Read-only single-cell counterfactual. Never runs collision movement or edits Tiles.
internal static class MiningSupportImpact
{
    internal sealed record Result(bool Available, int TargetX, int TargetY, PhysicsState Pose,
        MotionSupport.Assessment Current, MotionSupport.Assessment AfterRemoval,
        bool? AffectsCurrentStance, float? SupportDeltaPixels, string[] Reasons)
    {
        public float MinimumSupportPixels => MotionSupport.MinimumSupportPixels;
        public string Scope => "current fixed 20x42 normal-gravity pose only; grounded flag held constant; single target cell removal; not mining permission, full navigation safety, falling/landing prediction or return-route proof; resample after terrain/pose changes";
    }
    internal static Result Read(PhysicsState pose,int x,int y,Func<int,int,char> readCell,string unsupported=null)
    {
        if(unsupported!=null||!float.IsFinite(pose.Px)||!float.IsFinite(pose.Py))
            return new(false,x,y,pose,null,null,null,null,new[]{unsupported??"non_finite_pose"});
        // Freeze each sampled category for both comparisons, including unchanged neighbours.
        var cells=new Dictionary<(int,int),char>();
        char Read(int tx,int ty)
        {
            if(!cells.TryGetValue((tx,ty),out char value))
            {
                try { value=readCell(tx,ty); } catch { value='?'; }
                cells[(tx,ty)]=value;
            }
            return value;
        }
        var current=MotionSupport.Assess(pose,Read);
        char target=Read(x,y);
        // Removing a tile does not remove its liquid/hazard or resolve unknown Mod collision.
        if(target is '?' or '!')
            return new(false,x,y,pose,current,null,null,null,new[]{target=='?'?"target_terrain_unknown":"target_liquid_or_hazard_not_modelled"});
        var after=MotionSupport.Assess(pose,(tx,ty)=>tx==x&&ty==y?'.':Read(tx,ty));
        bool known=current.SupportPixels.HasValue&&after.SupportPixels.HasValue;
        var reasons=new List<string>();
        if(!current.SupportPixels.HasValue)reasons.Add("current: "+current.Rejection);
        if(!after.SupportPixels.HasValue)reasons.Add("after_removal: "+after.Rejection);
        return new(known,x,y,pose,current,after,
            known?current.SupportPixels!=after.SupportPixels||current.Rejection!=after.Rejection:null,
            known?after.SupportPixels-current.SupportPixels:null,reasons.ToArray());
    }
}
