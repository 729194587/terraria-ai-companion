using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CompanionBridge.Motion;

public static class MiningSupportImpactTests
{
    public static void Run(string fixturePath)
    {
        int checks=0;
        void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
        void Width(float? actual,float expected,string message)=>Check(actual.HasValue&&Math.Abs(actual.Value-expected)<0.005f,message);
        var world=new Dictionary<(int,int),char>();
        char Read(int x,int y)=>world.TryGetValue((x,y),out var c)?c:'.';
        var pose=new PhysicsState(12,118,0,0,true); // feet=160; overlap left 4, right 16.
        world[(0,10)]='#';world[(1,10)]='#';
        var narrow=MiningSupportImpact.Read(pose,1,10,Read);
        Width(narrow.Current.SupportPixels,20,"current full support");Width(narrow.AfterRemoval.SupportPixels,4,"single removal leaves 4px");
        Check(narrow.Available&&narrow.AffectsCurrentStance==true&&narrow.SupportDeltaPixels==-16,"narrow support impact explicit");
        Check(narrow.AfterRemoval.Rejection.Contains("4.000px < 8px"),"same navigation rejection");
        Check(world[(1,10)]=='#',"counterfactual did not mutate terrain");
        world[(3,9)]='#';var side=MiningSupportImpact.Read(pose,3,9,Read);
        Check(side.Available&&side.AffectsCurrentStance==false&&side.SupportDeltaPixels==0,"ordinary side mining unchanged");
        var wide=new PhysicsState(16,118,0,0,true);
        var down=MiningSupportImpact.Read(wide,1,10,Read);
        Width(down.Current.SupportPixels,16,"downward before support");Width(down.AfterRemoval.SupportPixels,0,"downward current pose loses contact");
        Check(down.Available&&down.Pose==wide&&down.Scope.Contains("not mining permission")&&down.Scope.Contains("falling/landing"),"zero support is not a fall prediction or rejection policy");
        world[(0,10)]='?';var unknown=MiningSupportImpact.Read(pose,1,10,Read);
        Check(!unknown.Available&&unknown.AffectsCurrentStance==null&&unknown.Current.SupportPixels==null&&unknown.Reasons.Length>0,"unknown neighbour stays unknown");
        var unknownTarget=MiningSupportImpact.Read(wide,5,10,(x,y)=>x==5&&y==10?'?':Read(x,y));
        Check(!unknownTarget.Available&&unknownTarget.AfterRemoval==null,"unknown target is not optimistically removed");
        var hazard=MiningSupportImpact.Read(wide,1,10,(x,y)=>x==1&&y==10?'!':'.');
        Check(!hazard.Available&&hazard.Reasons[0]=="target_liquid_or_hazard_not_modelled","tile removal cannot erase liquid/hazard");
        world[(0,10)]='#';world[(1,10)]='.';
        var changed=MiningSupportImpact.Read(pose,1,10,Read);
        Width(changed.Current.SupportPixels,4,"fresh assessment sees changed world");
        Check(changed.AffectsCurrentStance==false,"already-empty target has no new support effect");
        var reads=new HashSet<(int,int)>();int count=0;
        MiningSupportImpact.Read(pose,1,10,(x,y)=>{count++;reads.Add((x,y));return Read(x,y);});
        Check(count==reads.Count,"each sampled cell frozen once across both assessments");
        Check(!MiningSupportImpact.Read(pose,1,10,Read,"unsupported_body_dimensions").Available,"unsupported profile explicit");
        Check(!MiningSupportImpact.Read(pose with{Grounded=false},1,10,Read).Available,"airborne pose not treated as settled stance");
        Check(!MiningSupportImpact.Read(pose,1,10,(x,y)=>throw new IOException("fixture")).Available,"read failure becomes unknown, no speculative empty cell");
        // Fractional overlap fixture only: not an assertion about v6's historical terrain.
        var fractional=new PhysicsState(35326.562f,3414,0,0,true);
        var fr=MiningSupportImpact.Read(fractional,2209,216,(x,y)=>x==2209&&y==216?'#':'.');
        Width(fr.Current.SupportPixels,2.5625f,"fractional narrow support represented without rounding");
        Width(fr.AfterRemoval.SupportPixels,0,"fractional support removal");
        // Earlier recorded v3 geometry + logged v6 actions, holding each logged pre-mine pose fixed.
        using var doc=JsonDocument.Parse(File.ReadAllText(fixturePath));var root=doc.RootElement;
        int ox=root.GetProperty("originX").GetInt32(),oy=root.GetProperty("originY").GetInt32();
        var rows=root.GetProperty("rows");var removed=new HashSet<(int,int)>();
        char Recorded(int x,int y)=>removed.Contains((x,y))?'.':x<ox||y<oy||y>=oy+rows.GetArrayLength()||x>=ox+45?'?':rows[y-oy].GetString()[x-ox];
        var first=MiningSupportImpact.Read(new(35148,3654,0,0,true),2197,231,Recorded);
        Width(first.Current.SupportPixels,16,"v6 first before support");Width(first.AfterRemoval.SupportPixels,0,"v6 first fixed-pose removal is 0, not future 4");
        removed.Add((2197,231));
        var second=MiningSupportImpact.Read(new(35148,3670,0,0,true),2197,232,Recorded);
        Width(second.Current.SupportPixels,16,"v6 second before support");Width(second.AfterRemoval.SupportPixels,0,"v6 second fixed-pose removal is 0, not future 4");
        removed.Add((2197,232));
        var final=MiningSupportImpact.Read(new(35148,3678,0,0,true),2197,232,Recorded);
        Width(final.Current.SupportPixels,4,"v6 actual later pose matches 4px residual halfblock support");
        Check(final.AffectsCurrentStance==false,"already removed target does not directly remove the residual support");
        Check(first.AffectsCurrentStance==true&&second.AffectsCurrentStance==true,"assessment identifies loss-of-support risk before v6 removals");
        Console.WriteLine($"Mining support impact: {checks} checks passed; fixed-pose counterfactual only, not a native fall replay.");
        Console.WriteLine(JsonSerializer.Serialize(new{first,second,final},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
    }
}
