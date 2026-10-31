using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using CompanionBridge.Motion;

namespace CompanionBridge;

// Detached local facts. No Terraria access, path search or action selection.
internal sealed record SceneCell(TileCell Facts,char Navigation,string Kind=null,string ContentKey=null,int? AnchorX=null,int? AnchorY=null,string[] Sources=null);
internal sealed record SceneObject(string Id,string Kind,int? TypeId,string ContentKey,TileBounds ObservedBounds,
    int ObservedTileCount,bool Complete,object Details=null);
internal sealed record SceneLanding(string Id,LandingRegion Target,float FeetDeltaY);
internal sealed record ScenePlacement(string Id,object Target,object SolidNeighbor);
internal sealed record SceneObjectPlacement(string Id,object Target,TileBounds Footprint,TileBounds Support,float DistancePixels,
    string ContentKey="Terraria/WorkBench",int Style=0);
internal sealed record SceneHazard(string Id,string[] Sources,int MinX,int MaxX,int Y,int MinLiquidAmount,int MaxLiquidAmount,int? TileType,string ContentKey,int TouchDamage);
internal sealed record SceneList<T>(T[] Items,int MatchedCount,int ReturnedCount,bool Truncated,int OmittedCount);
internal sealed record SceneCandidateList<T>(T[] Items,int MatchedCount,int ReturnedCount,bool Truncated,int OmittedCount,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string Validation);
internal sealed record SceneObjectPlacementExample(object Target,TileBounds Footprint,TileBounds Support,object Detail);
internal sealed record SceneObjectPlacementDiagnostics(int AnchorCount,int InBoundsAnchorCount,int CheckedCount,int RejectedCount,
    int UnknownCount,int NotCheckedCount,bool CoveredScopeComplete,Dictionary<string,int> RejectedByReason,
    Dictionary<string,int> NotCheckedByReason,Dictionary<string,SceneObjectPlacementExample> FirstExamples,
    string Classification="mutually exclusive first failure after full footprint/support knowledge; observed top-left anchors only");
internal sealed record SceneObjectPlacementList(SceneObjectPlacement[] Items,int MatchedCount,int ReturnedCount,bool Truncated,
    int OmittedCount,SceneObjectPlacementDiagnostics Diagnostics,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string Validation);

internal static class SceneGeometry
{
    internal static string[] GeometryRows(SceneCell[,] cells)=>Enumerable.Range(0,cells.GetLength(1))
        .Select(y=>new string(Enumerable.Range(0,cells.GetLength(0)).Select(x=>Geometry(cells[x,y])).ToArray())).ToArray();
    internal static string[] MapRows(SceneCell[,] cells,string[] geometry,int actorX,int actorY,IEnumerable<(int X,int Y,char Glyph)> markers)
    {
        int w=cells.GetLength(0),h=cells.GetLength(1);
        var map=Enumerable.Range(0,h).Select(y=>Enumerable.Range(0,w)
            .Select(x=>cells[x,y].Kind==null?geometry[y][x]:Glyph(cells[x,y].Kind)).ToArray()).ToArray();
        foreach(var marker in markers)map[marker.Y][marker.X]=marker.Glyph;
        map[actorY][actorX]='@';
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            char danger=Danger(cells[x,y].Sources);
            if(danger!='\0')map[y][x]=danger;
            if(geometry[y][x]=='?')map[y][x]='?';
        }
        return map.Select(row=>new string(row)).ToArray();
    }
    internal static SceneList<T> Limit<T>(IEnumerable<T> items,int cap)
    {
        var all=items.ToArray();return new(all.Take(cap).ToArray(),all.Length,Math.Min(cap,all.Length),all.Length>cap,Math.Max(0,all.Length-cap));
    }
    // Decorative light density cannot displace any previously exposed kind.
    // Preserve distance ordering within both tiers and count all observations.
    internal static SceneList<SceneObject> LimitObjects(IEnumerable<SceneObject> items,int cap,Func<SceneObject,float> distance)
        =>Limit(items.OrderBy(o=>o.Kind=="light_source"?1:0).ThenBy(distance).ThenBy(o=>o.Id,StringComparer.Ordinal),cap);
    internal static SceneCandidateList<SceneLanding> LimitLandings(IEnumerable<SceneLanding> items,int cap)
    {
        var list=Limit(items,cap);return new(list.Items,list.MatchedCount,list.ReturnedCount,list.Truncated,list.OmittedCount,
            list.MatchedCount>0?"shared_navigation_support; integer-pixel interval; reachability not evaluated":null);
    }
    internal static SceneCandidateList<ScenePlacement> LimitPlacements(IEnumerable<ScenePlacement> items,int cap)
    {
        var list=Limit(items,cap);return new(list.Items,list.MatchedCount,list.ReturnedCount,list.Truncated,list.OmittedCount,
            list.MatchedCount>0?"empty, dry, known, player-clear, <=80px and solid cardinal neighbour; native rules not evaluated":null);
    }
    internal static char Geometry(SceneCell cell)
    {
        var c=cell.Facts;
        if(c.Knowledge!="read"||c.CollisionKnowledge!="known"||cell.Navigation=='?')return '?';
        if(c.HasTile&&c.Actuated)return 'A';
        return TerrainClassifier.Classify(true,c.HasTile,c.Actuated,c.Solid,c.SolidTop,c.FrameY,c.HalfBlock,c.Slope,false,false);
    }
    internal static char Glyph(string kind)=>kind switch{"tree_component"=>'T',"ore_component"=>'O',"chest"=>'C',"door"=>'D',"workbench"=>'W',"light_source"=>'t',"drop"=>'$',"npc"=>'N',"player"=>'P',_=>'+'};
    internal static char Danger(string[] sources)
    {
        if(sources==null||sources.Length==0)return '\0';
        if(sources.Contains("unknown_liquid"))return '?';
        if(sources.Contains("lava"))return 'L';
        if(sources.Contains("shimmer"))return 'S';
        if(sources.Contains("boulder_tile"))return 'B';
        if(sources.Any(s=>s.StartsWith("touch_",StringComparison.Ordinal)))return '!';
        if(sources.Contains("water"))return '~';
        if(sources.Contains("honey"))return 'H';
        return 'M';
    }
    internal static SceneObject[] Objects(SceneCell[,] cells,int ox,int oy)
    {
        int w=cells.GetLength(0),h=cells.GetLength(1);var seen=new HashSet<(int,int)>();var output=new List<SceneObject>();
        var anchors=new Dictionary<(string,int,int,int),List<(int,int)>>();
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            var c=cells[x,y];if(c.Kind==null||seen.Contains((x,y)))continue;
            var group=new List<(int x,int y)>();bool complete=c.Kind=="ore_component";
            if(c.Kind is "tree_component" or "ore_component")
            {
                var queue=new Queue<(int,int)>();queue.Enqueue((x,y));seen.Add((x,y));
                while(queue.TryDequeue(out var p))
                {
                    group.Add(p);
                    foreach(var d in new[]{(-1,0),(1,0),(0,-1),(0,1)})
                    {
                        int nx=p.Item1+d.Item1,ny=p.Item2+d.Item2;
                        if(nx<0||ny<0||nx>=w||ny>=h){complete=false;continue;}
                        var next=cells[nx,ny];if(Geometry(next)=='?')complete=false;
                        if(next.Kind==c.Kind&&next.Facts.TileType==c.Facts.TileType&&seen.Add((nx,ny)))queue.Enqueue((nx,ny));
                    }
                }
            }
            else
            {
                var key=(c.Kind,c.Facts.TileType??-1,c.AnchorX??x+ox,c.AnchorY??y+oy);
                if(!anchors.TryGetValue(key,out var list))anchors[key]=list=new();list.Add((x,y));continue;
            }
            Add(c,group,complete);
        }
        foreach(var pair in anchors)
            Add(cells[pair.Value[0].Item1,pair.Value[0].Item2],pair.Value,pair.Key.Item1=="light_source");
        return output.ToArray();
        void Add(SceneCell c,List<(int x,int y)> group,bool complete)
        {
            int minX=group.Min(p=>p.x),maxX=group.Max(p=>p.x),minY=group.Min(p=>p.y),maxY=group.Max(p=>p.y);
            var bottom=group.OrderByDescending(p=>p.y).ThenBy(p=>p.x).First();
            output.Add(new("o"+(output.Count+1),c.Kind,c.Facts.TileType,c.ContentKey,new(minX+ox,minY+oy,maxX-minX+1,maxY-minY+1),
                group.Count,complete,c.Kind=="light_source"
                    ?new{anchorTile=new{x=minX+ox,y=minY+oy},frameX=c.Facts.FrameX,frameY=c.Facts.FrameY,actuated=c.Facts.Actuated}
                    :(object)new{anchorTile=new{x=c.AnchorX??minX+ox,y=c.AnchorY??minY+oy},
                        observedTargetTile=new{x=bottom.x+ox,y=bottom.y+oy}}));
        }
    }
    internal static SceneLanding[] Landings(SceneCell[,] cells,int ox,int oy,float actorX,float feetY,Func<PhysicsState,bool> nativeSafe=null)
    {
        int w=cells.GetLength(0),h=cells.GetLength(1);var output=new List<SceneLanding>();
        char Read(int x,int y)=>x<ox||y<oy||x>=ox+w||y>=oy+h?'?':cells[x-ox,y-oy].Navigation;
        for(int row=0;row<h;row++)
        {
            // Multiple cave/platform levels remain separate; no fixed-foot-height scan.
            if(!Enumerable.Range(0,w).Any(x=>Read(x+ox,row+oy) is '#' or '='))continue;
            int? start=null;
            for(int px=ox*16;px<=((ox+w)*16-PhysicsSimulator.PlayerW);px++)
            {
                var s=new PhysicsState(px,(oy+row)*16-PhysicsSimulator.PlayerH,0,0,true);
                bool valid=MotionSupport.SafeFlatLanding(s,Read)&&(nativeSafe?.Invoke(s)??true);
                if(valid){start??=px;continue;}
                if(start.HasValue){Add(start.Value,px-1);start=null;}
            }
            if(start.HasValue)Add(start.Value,(ox+w)*16-PhysicsSimulator.PlayerW);
            void Add(int min,int max)=>output.Add(new("l"+(output.Count+1),new(min,max,(row+oy)*16),(row+oy)*16-feetY));
        }
        return output.OrderBy(r=>Math.Max(0,Math.Max(r.Target.MinPlayerLeftX-actorX,actorX-r.Target.MaxPlayerLeftX))+Math.Abs(r.FeetDeltaY)).ThenBy(r=>r.Target.SurfaceY).ThenBy(r=>r.Target.MinPlayerLeftX).ToArray();
    }
    internal static ScenePlacement[] Placements(SceneCell[,] cells,int ox,int oy,WorldRect actor,IReadOnlyList<WorldRect> players)
    {
        int w=cells.GetLength(0),h=cells.GetLength(1);var output=new List<(float,ScenePlacement)>();
        for(int y=1;y<h-1;y++)for(int x=1;x<w-1;x++)
        {
            var c=cells[x,y];if(Geometry(c)=='?'||c.Facts.HasTile||c.Facts.LiquidAmount>0)continue;
            int tx=x+ox,ty=y+oy;float dx=tx*16+8-(actor.X+actor.Width/2),dy=ty*16+8-(actor.Y+actor.Height/2);
            float distance=dx*dx+dy*dy;if(distance>80*80)continue;
            if(players.Any(p=>tx*16<p.X+p.Width&&(tx+1)*16>p.X&&ty*16<p.Y+p.Height&&(ty+1)*16>p.Y))continue;
            foreach(var d in new[]{(0,1),(-1,0),(1,0),(0,-1)})
            {
                var next=cells[x+d.Item1,y+d.Item2];
                if(Geometry(next)!='#'||next.Navigation!='#')continue;
                output.Add((distance,new("p"+(output.Count+1),new{x=tx,y=ty},new{x=tx+d.Item1,y=ty+d.Item2})));break;
            }
        }
        return output.OrderBy(p=>p.Item1).Select(p=>p.Item2).ToArray();
    }
    internal static SceneObjectPlacement[] ObjectPlacements(SceneCell[,] cells,int ox,int oy,WorldRect actor,IReadOnlyList<WorldRect> players,
        int width,int height,int originX,int originY,int supportStart,int supportCount)
        =>ObjectPlacements(cells,ox,oy,actor,players,width,height,originX,originY,supportStart,supportCount,out _);

    internal static SceneObjectPlacementList LimitObjectPlacements(SceneObjectPlacement[] items,int cap,SceneObjectPlacementDiagnostics diagnostics)
    {
        var limited=Limit(items,cap);
        return new(limited.Items,limited.MatchedCount,limited.ReturnedCount,limited.Truncated,limited.OmittedCount,diagnostics,
            limited.MatchedCount>0?"known dry empty full footprint, known dry flat solid support, player-clear, anchor <=80px; native rules not evaluated":null);
    }
    internal static SceneObjectPlacement[] ObjectPlacements(SceneCell[,] cells,int ox,int oy,WorldRect actor,IReadOnlyList<WorldRect> players,
        int width,int height,int originX,int originY,int supportStart,int supportCount,out SceneObjectPlacementDiagnostics diagnostics)
    {
        var output=new List<SceneObjectPlacement>();int w=cells.GetLength(0),h=cells.GetLength(1);
        var rejected=new Dictionary<string,int>{{"footprint_occupied",0},{"liquid_present",0},{"missing_support",0},{"out_of_reach",0},{"player_collision",0}};
        var notChecked=new Dictionary<string,int>{{"outside_observation",0},{"definition_unavailable",0}};
        var examples=new Dictionary<string,SceneObjectPlacementExample>();
        int inBounds=0,checkedCount=0,unknown=0;
        bool definition=width>0&&height>0&&supportStart>=0&&supportCount>0&&supportStart+supportCount<=width;
        if(!definition)notChecked["definition_unavailable"]=w*h;
        else for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            int tx=x+ox+originX,ty=y+oy+originY;
            var footprint=new TileBounds(x+ox,y+oy,width,height);
            var support=new TileBounds(x+ox+supportStart,y+oy+height,supportCount,1);
            void Example(string reason,object detail)
            {if(!examples.ContainsKey(reason))examples[reason]=new(new{x=tx,y=ty},footprint,support,detail);}
            void Reject(string reason,object detail){rejected[reason]++;Example(reason,detail);}
            if(x+width>w||y+height>=h)
            {notChecked["outside_observation"]++;Example("outside_observation",null);continue;}
            inBounds++;
            // Unknown takes precedence over all confirmed failures, even if a
            // different, known cell would already reject this anchor.
            object unknownCell=null,occupied=null,liquid=null,missingSupport=null;
            object Fact(int cx,int cy,SceneCell c)=>new{x=cx+ox,y=cy+oy,tileType=c.Facts.TileType,
                knowledge=c.Facts.Knowledge,collisionKnowledge=c.Facts.CollisionKnowledge,navigation=c.Navigation.ToString(),liquidAmount=c.Facts.LiquidAmount};
            for(int dx=0;dx<width;dx++)for(int dy=0;dy<height;dy++)
            {
                var c=cells[x+dx,y+dy];
                if(Geometry(c)=='?')unknownCell??=Fact(x+dx,y+dy,c);
                if(c.Facts.HasTile)occupied??=Fact(x+dx,y+dy,c);
                if(c.Facts.LiquidAmount>0)liquid??=Fact(x+dx,y+dy,c);
            }
            for(int dx=supportStart;dx<supportStart+supportCount;dx++)
            {
                var c=cells[x+dx,y+height];char shape=Geometry(c);
                if(shape=='?')unknownCell??=Fact(x+dx,y+height,c);
                if(shape!='#'||c.Navigation!='#')missingSupport??=Fact(x+dx,y+height,c);
                if(c.Facts.LiquidAmount>0)liquid??=Fact(x+dx,y+height,c);
            }
            if(unknownCell!=null){unknown++;Example("unknown",unknownCell);continue;}
            checkedCount++;
            if(occupied!=null){Reject("footprint_occupied",occupied);continue;}
            if(liquid!=null){Reject("liquid_present",liquid);continue;}
            if(missingSupport!=null){Reject("missing_support",missingSupport);continue;}
            float ax=tx*16+8-(actor.X+actor.Width/2),ay=ty*16+8-(actor.Y+actor.Height/2);
            float distance=MathF.Sqrt(ax*ax+ay*ay);
            if(distance>80){Reject("out_of_reach",new{distancePixels=distance,limitPixels=80});continue;}
            var collision=players.FirstOrDefault(p=>(x+ox)*16<p.X+p.Width&&(x+ox+width)*16>p.X&&(y+oy)*16<p.Y+p.Height&&(y+oy+height)*16>p.Y);
            if(collision!=null){Reject("player_collision",new{playerBoundsWorldPixels=collision});continue;}
            output.Add(new("b"+(output.Count+1),new{x=tx,y=ty},footprint,support,distance));
        }
        diagnostics=new(w*h,inBounds,checkedCount,rejected.Values.Sum(),unknown,notChecked.Values.Sum(),definition&&inBounds>0&&unknown==0,rejected,notChecked,examples);
        return output.OrderBy(c=>c.DistancePixels).ToArray();
    }
    internal static SceneHazard[] Hazards(SceneCell[,] cells,int ox,int oy)
    {
        var output=new List<SceneHazard>();int w=cells.GetLength(0),h=cells.GetLength(1);
        for(int y=0;y<h;y++)for(int x=0;x<w;)
        {
            var sources=cells[x,y].Sources;
            if(sources==null||sources.Length==0){x++;continue;}
            var firstCell=cells[x,y];
            int first=x,min=firstCell.Facts.LiquidAmount,max=min;x++;
            while(x<w&&(cells[x,y].Sources??Array.Empty<string>()).SequenceEqual(sources)&&
                cells[x,y].Facts.TileType==firstCell.Facts.TileType&&cells[x,y].Facts.TouchDamage==firstCell.Facts.TouchDamage)
            {min=Math.Min(min,cells[x,y].Facts.LiquidAmount);max=Math.Max(max,cells[x,y].Facts.LiquidAmount);x++;}
            output.Add(new("h"+(output.Count+1),sources,ox+first,ox+x-1,oy+y,min,max,firstCell.Facts.TileType,firstCell.ContentKey,firstCell.Facts.TouchDamage));
        }
        return output.ToArray();
    }
}
