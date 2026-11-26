using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// A plan ends at the first safe, quotable ore strike. Each edge owns its
// overlay, captured reads and settled poses. No cross-overlay route cache.
internal sealed class OreApproachSearch(AiReadOnlyProtocol.Identity identity, PhysicsState start,
    PhysicsParameters physics, ExcavationCell[] targets, NativeNavigationEdits edits,int remainingCuts)
{
    internal sealed record Cost(float Movement,float Clearance,float Target,int Cuts)
    {
        public float Alteration=>Cuts*4;
        public float Total=>Movement+Clearance+Target+Alteration;
    }
    internal sealed record Plan(LocalEditAction[] Actions,Cost Cost,ExcavationCell Goal,
        Dictionary<(int X,int Y),TerrainCell> Dependencies);
    private sealed record Node(PhysicsState Pose,LocalTerrainView View,List<LocalEditAction> Actions,
        float Movement,float Mining,int Cuts,HashSet<(int,int)> Reads);

    internal Plan Walk,Clearance;
    internal bool Unsupported,Limited;
    private readonly Dictionary<(int,int),TerrainCell> snapshot=new();
    private HashSet<(int,int)> reads;
    private TerrainCell Read(int x,int y)
    {
        reads.Add((x,y));
        if(!snapshot.TryGetValue((x,y),out var cell))
        {
            int dx=x-(int)((start.Px+10)/16),dy=y-(int)((start.Py+42)/16);
            snapshot[(x,y)]=cell=dx is >=-22 and <=22 && dy is >=-17 and <=11
                ?edits.Environment(x,y):new('?',default);
        }
        return cell;
    }
    private OverlayMotionWorld World(LocalTerrainView view)=>new(view,TerrainReader.OrdinaryOverlayCell);
    private float Distance(PhysicsState p)=>targets.Min(c=>
        Math.Abs(c.X*16+8-p.Px-10)+Math.Abs(c.Y*16+8-p.Py-21));

    private bool Goal(Node node)
    {
        var world=World(node.View);
        if(!world.SafeLanding(node.Pose))return false;
        Plan best=node.Cuts>0?Clearance:Walk;
        bool reached=false;
        foreach(var cell in targets.OrderBy(c=>Math.Abs(c.X*16-node.Pose.Px)+Math.Abs(c.Y*16-node.Pose.Py)))
        {
            if(node.Cuts>0&&!NativeNavigationEdits.SafeRemovalNeighbours(cell.X,cell.Y,node.View))continue;
            var quote=NativeExcavation.Choose(identity,node.Pose,new[]{cell},node.View.Fork(),false);
            if(quote==null)continue;
            reached=true;
            var cost=new Cost(node.Movement,node.Mining,quote.RunUpdates,node.Cuts);
            if(best==null||cost.Total<best.Cost.Total)
                best=new(node.Actions.ToArray(),cost,cell,reads.ToDictionary(k=>k,k=>snapshot[k]));
        }
        if(node.Cuts>0)Clearance=best;else Walk=best;
        return reached;
    }
    private Node Cut(Node node,int x,int y,bool allowFall)
    {
        var pose=node.Pose;
        var cell=node.View.Environment(x,y);
        if(node.Cuts>=remainingCuts){Limited=true;return null;}
        if(cell.TileType is not int type||TileID.Sets.Ore[type]
            ||!TerrainReader.OrdinaryOverlayCell(cell)||identity.Autonomy!=null&&AutonomyPolicy.ProtectedTile(x,y))return null;
        var quote=edits.Mine(pose,x,y,node.View);
        if(quote==null)return null;
        var view=node.View.Fork();
        if(!view.Set(x,y,'.'))return null;
        var world=World(view);
        PhysicsState next=pose;
        int fallUpdates=0;
        if(!world.SafeLanding(pose))
        {
            if(!allowFall)return null;
            var fall=PhysicsSimulator.Predict(pose,0,0,physics,world,walkFrames:0);
            if(!fall.Safe||fall.Landing.Py<=pose.Py)return null;
            next=fall.Landing;fallUpdates=fall.Frames.Count;
        }
        var actions=new List<LocalEditAction>(node.Actions){new(LocalEditKind.Mine,pose,next,x,y,quote.Slot,type)};
        return new(next,view,actions,node.Movement+fallUpdates,node.Mining+quote.Updates,node.Cuts+1,reads);
    }
    private Node Advance(Node node,int direction,int stride,int hold,bool clear)
    {
        if(clear)
        {
            // Find the exact body envelope of the requested flat stride, including
            // braking, before asking which live solids obstruct this movement.
            int row=(int)Math.Round((node.Pose.Py+42)/16);
            var flat=new LocalTerrainView((x,y)=>y>=row?'#':'.');
            var free=PhysicsSimulator.Predict(node.Pose,direction,0,physics,World(flat),walkFrames:stride);
            if(!free.Safe)return null;
            var box=BodyTileBounds.FromPixels(Math.Min(node.Pose.Px,free.Landing.Px),node.Pose.Py,
                Math.Abs(free.Landing.Px-node.Pose.Px)+20,42);
            for(int i=box.MinX;i<=box.MaxX;i++)
            for(int y=box.MinY;y<=box.MaxY;y++)
            {
                int x=direction>0?i:box.MaxX-(i-box.MinX);
                if(node.View.Cell(x,y)=='.')continue;
                node=Cut(node,x,y,false);
                if(node==null)return null;
                if(Goal(node))return node;
            }
        }
        var edge=PhysicsSimulator.Predict(node.Pose,direction,hold,physics,World(node.View),walkFrames:stride);
        if(!edge.Safe)
        {
            Unsupported|=edge.Reason.StartsWith("unsupported",StringComparison.Ordinal);
            return null;
        }
        return node with{Pose=edge.Landing,Movement=node.Movement+edge.Frames.Count,Reads=reads,
            Actions=new(node.Actions){new(LocalEditKind.Move,node.Pose,edge.Landing)}};
    }
    private Node Down(Node node)
    {
        int row=(int)Math.Round((node.Pose.Py+42)/16);
        int left=(int)Math.Floor(node.Pose.Px/16),right=(int)Math.Ceiling((node.Pose.Px+20)/16);
        var columns=Enumerable.Range(left,right-left).OrderBy(x=>
            Math.Min(node.Pose.Px+20,(x+1)*16)-Math.Max(node.Pose.Px,x*16)).ToArray();
        foreach(int x in columns)
        {
            if(node.View.Cell(x,row)=='.')continue;
            node=Cut(node,x,row,true);
            if(node==null)return null;
            if(Goal(node)||node.Pose.Py+42>row*16+1)return node;
        }
        return null;
    }

    internal IEnumerable<int> Run()
    {
        if(!physics.IsBasic||physics.Air!=null||physics.ExtraJump!=null||physics.Impulse!=null||start.Wet)
        {Unsupported=true;yield break;}
        reads=new();
        var root=new Node(start,new LocalTerrainView(Read),new(),0,0,0,reads);
        if(!World(root.View).SafeLanding(start)){Unsupported=true;yield break;}
        var frontier=new List<Node>{root};
        int expanded=0;
        for(int depth=0;depth<LocalEditEvaluation.MaxSteps&&frontier.Count>0;depth++)
        {
            var next=new List<Node>();
            foreach(var node in frontier.OrderBy(n=>Distance(n.Pose)/physics.MaxRun+n.Movement+n.Mining))
            {
                if(++expanded>32){Limited=true;yield break;}
                var nearest=targets.MinBy(c=>Math.Abs(c.X*16-node.Pose.Px)+Math.Abs(c.Y*16-node.Pose.Py));
                int direction=nearest.X*16+8>=node.Pose.Px+10?1:-1;
                bool above=nearest.Y*16>node.Pose.Py+42;
                // A straight shaft has a single continuation. Explore it without
                // consuming a branching level for every row. Mine still checks
                // the original width, depth, per-cut reach and total cut limit.
                if(above&&node.Cuts==0&&Math.Abs(nearest.X*16-node.Pose.Px)<=4*16)
                {
                    reads=new(node.Reads);
                    var shaft=node;
                    while(shaft.Cuts<remainingCuts)
                    {
                        var lower=Down(shaft);
                        yield return 0;
                        if(lower==null||Goal(lower)||lower.Pose.Py<=shaft.Pose.Py)break;
                        shaft=lower;
                    }
                }
                foreach(var (stride,hold,clear) in new[]{(24,0,false),(16,0,false),(24,5,false),(24,physics.JumpHeight,false),(24,0,true),(16,0,true),(0,0,true),(-16,0,false)})
                {
                    if(stride<0&&!above)continue;
                    reads=new(node.Reads);
                    var child=stride==0?Down(node):Advance(node,direction*Math.Sign(stride),Math.Abs(stride),hold,clear);
                    yield return 0;
                    if(child==null||Goal(child))continue;
                    if(child.Pose==node.Pose||Distance(child.Pose)>=Distance(node.Pose)
                        &&!(above&&depth<2&&child.Pose.Py==node.Pose.Py&&child.Cuts==0))continue;
                    // No identical walk-only and walk-with-empty-corridor siblings.
                    if(next.Any(n=>n.Pose==child.Pose&&n.Actions.SequenceEqual(child.Actions)))continue;
                    next.Add(child);
                }
            }
            frontier=next;
        }
        Limited|=frontier.Count>0;
    }
}
