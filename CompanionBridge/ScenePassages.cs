using System;
using System.Collections.Generic;
using System.Linq;
using CompanionBridge.Motion;

namespace CompanionBridge;

internal sealed record PassageExit(string Kind, string Direction, SceneTilePoint Target, string LandingId);
internal sealed record SurveyRoute(string Direction,string Kind,SceneTilePoint Target,string VisitState,int? UnknownNearbyTiles);
internal sealed record PassageSurvey(bool OriginResolved, SceneTilePoint ReturnTarget, bool Junction,
    PassageExit[] Exits, string[] ConnectedLandingIds, SceneTilePoint OpenShaft, SceneTilePoint DescentStep, SurveyRoute[] Routes);

// A side-view survey of dry body-sized space, independent of jumping ability.
// Positions in the public result are navigate_to foot points in world pixels.
internal static class ScenePassages
{
    internal static PassageSurvey Read(SceneCell[,] cells, int ox, int oy, float left, float feet, SceneLanding[] landings,
        Func<int,int,bool> walkedNear=null,Func<int,int,int> unknownAround=null)
    {
        int width = cells.GetLength(0), height = cells.GetLength(1);
        int sx = (int)Math.Round((left + PhysicsSimulator.PlayerW / 2f) / 16) - ox;
        int sy = (int)Math.Floor(feet / 16) - oy;
        bool Known(int x, int y) => cells[x,y].Facts is { Knowledge: "read", CollisionKnowledge: "known" };
        var open = new bool[width, height + 1];
        for (int x = 1; x < width; x++)
        for (int y = 3; y <= height; y++)
        {
            // Centered on the seam between columns x-1 and x, feet at row y.
            bool clear = true;
            for (int i = x - 1; i <= x && clear; i++)
            for (int j = y - 3; j < y; j++)
                if (!Known(i,j) || cells[i,j].Navigation is not ('.' or '=' or 'A') || cells[i,j].Sources is {Length: >0})
                { clear = false; break; }
            open[x,y] = clear;
        }
        if (sx < 1 || sx >= width || sy < 3 || sy > height || !open[sx,sy])
            return new(false, null, false, Array.Empty<PassageExit>(), Array.Empty<string>(), null, null, Array.Empty<SurveyRoute>());

        int Distance(int x, int y) => Math.Abs(x - sx) + Math.Abs(y - sy);
        bool Roof(int x, int y)
        {
            for (int j = y - 4; j >= Math.Max(0, y - 12); j--)
            {
                char a = cells[x-1,j].Navigation, b = cells[x,j].Navigation;
                if (!Known(x-1,j) || !Known(x,j) || a == '?' || b == '?') break;
                if (a == '#' && b == '#') return true;
            }
            return false;
        }
        var component = new int[width,height+1];
        var queue = new Queue<(int X,int Y)>();
        void Flood(int x, int y, int previous, int label, bool outside)
        {
            void Add(int i, int j)
            {
                if (i < 1 || i >= width || j < 3 || j > height || !open[i,j]
                    || component[i,j] != previous || outside && Distance(i,j) < 6) return;
                component[i,j] = label;
                queue.Enqueue((i,j));
            }
            Add(x,y);
            while (queue.TryDequeue(out var p))
            { Add(p.X-1,p.Y); Add(p.X+1,p.Y); Add(p.X,p.Y-1); Add(p.X,p.Y+1); }
        }
        Flood(sx,sy,0,1,false);
        // Removing the immediate vicinity separates arms of a local fork. Two
        // ends of a straight tunnel are ordinary continuations.
        int label = 1;
        for (int x = 1; x < width; x++)
        for (int y = 3; y <= height; y++)
            if (component[x,y] == 1 && Distance(x,y) >= 6) Flood(x,y,1,++label,true);

        var connected = new List<string>();
        var exits = new Dictionary<int,(int Priority, int Distance, PassageExit Exit)>();
        bool coveredHere = Roof(sx,sy);
        SceneTilePoint here = null;
        SceneTilePoint descent = null;
        var lateral = new Dictionary<string,(int Cost,SceneTilePoint Target)>();
        int stepDistance = int.MaxValue;
        foreach (var row in landings)
        {
            var region = row.Target;
            if (left >= region.MinPlayerLeftX && left <= region.MaxPlayerLeftX && Math.Abs(feet-region.SurfaceY) <= .5)
                here = new((int)Math.Round(left + PhysicsSimulator.PlayerW/2f), (int)region.SurfaceY);
            int y = (int)region.SurfaceY/16 - oy;
            bool meets = false;
            for (int x = Math.Max(1,(int)Math.Ceiling((region.MinPlayerLeftX+PhysicsSimulator.PlayerW/2f)/16)-ox);
                 x < width && (x+ox)*16 - PhysicsSimulator.PlayerW/2f <= region.MaxPlayerLeftX; x++)
            {
                int arm = component[x,y];
                if (arm == 0) continue;
                meets = true;
                // An open plain can be a single connected component in both directions.
                // Keep a representative stance on each side in addition to distinct arms.
                if (Math.Abs(x-sx)>=4 && Math.Abs(y-sy)<=4)
                {
                    string side=x<sx?"left":"right";
                    int cost=Math.Abs(Math.Abs(x-sx)-32)+3*Math.Abs(y-sy);
                    if(!lateral.TryGetValue(side,out var prior)||cost<prior.Cost)
                        lateral[side]=(cost,new((ox+x)*16,(oy+y)*16));
                }
                // The endpoint under an overhang can be almost directly below
                // the player with a solid floor between them. First target an
                // exposed lower ledge in the mouth, before turning underground.
                if (y > sy && Distance(x,y) < stepDistance &&
                    Enumerable.Range(sy,y-sy+1).All(j=>open[x,j]))
                {
                    descent = new((ox+x)*16,(oy+y)*16);
                    stepDistance = Distance(x,y);
                }
                if (arm == 1) continue;
                bool roof = Roof(x,y);
                string kind = roof && !coveredHere ? "possible_cave_entrance"
                    : y >= sy+3 ? (roof ? "covered_descent" : "open_descent")
                    : roof ? "covered_passage" : "open_landing";
                int priority = kind switch { "possible_cave_entrance" => 0, "covered_descent" => 1,
                    "open_descent" => 2, "covered_passage" => 3, _ => 4 };
                int distance = Math.Abs(Distance(x,y)-10);
                if (exits.TryGetValue(arm,out var old) && (old.Priority,old.Distance).CompareTo((priority,distance)) <= 0) continue;
                string direction = Math.Abs(y-sy) > Math.Abs(x-sx) ? (y>sy?"down":"up") : (x>sx?"right":"left");
                exits[arm] = (priority,distance,new(kind,direction,new((x+ox)*16,(y+oy)*16),row.Id));
            }
            if (meets) connected.Add(row.Id);
        }
        var branches = exits.Values.OrderBy(e=>e.Priority).ThenBy(e=>e.Distance).Select(e=>e.Exit).Take(8).ToArray();
        SceneTilePoint shaft = null;
        for (int x = Math.Max(1,sx-6); x <= Math.Min(width-1,sx+6) && shaft == null; x++)
            if (height-sy >= 8 && component[x,sy] != 0 &&
                Enumerable.Range(sy,height-sy+1).All(y=>open[x,y] && cells[x,y-1].Navigation == '.' && cells[x-1,y-1].Navigation == '.'))
                shaft = new((x+ox)*16,(oy+sy)*16);
        // At the bottom of a skylit shaft the exterior can reconnect around
        // the sampled neighborhood. Two distinct roofed arms still give the
        // descending actor a real choice even when there is no third component.
        bool junction = exits.Count >= 3 && branches.Any(e=>e.Kind!="open_landing")
            || branches.Count(e=>e.Kind=="possible_cave_entrance") >= 2;
        var routes=new List<SurveyRoute>();
        void AddRoute(string direction,string kind,SceneTilePoint target)
        {
            if(routes.Any(r=>r.Direction==direction && r.Target==target))return;
            int x=target.X/16,y=target.Y/16;
            string state=walkedNear==null?"unknown":walkedNear(x,y)?"walked_nearby":"no_recent_visit";
            routes.Add(new(direction,kind,target,state,unknownAround?.Invoke(x,y)));
        }
        if(descent!=null)AddRoute("down","lower_landing",descent);
        foreach(var branch in branches)AddRoute(branch.Direction,branch.Kind,branch.Target);
        foreach(var (side,option) in lateral)AddRoute(side,"lateral_landing",option.Target);
        return new(true,here,junction,branches,connected.ToArray(),shaft,descent,routes.ToArray());
    }
}
