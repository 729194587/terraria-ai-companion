using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;

namespace CompanionBridge.ServerIpc;

// Bounded, observation-scoped target leases. No hidden world search in the model.
internal static class SceneMineTargets
{
    internal sealed record Target(string kind,int x,int y,int typeId,string worldSessionId,long actorGeneration,int actorSlot,
        ulong observedGameUpdate,long observedAtUnixMs,string targetToken);
    private sealed record Entry(Target Target,object Actor,long Expires,string Shape,long Revision);
    private static readonly Dictionary<string,Entry> entries=new();
    private static readonly Dictionary<(int,int),long> revisions=new();
    private static string world;
    private static long generation,sequence;
    private static void Scope(AiReadOnlyProtocol.Identity id)
    {if(world==id.World&&generation==id.Generation)return;entries.Clear();revisions.Clear();world=id.World;generation=id.Generation;}
    internal static string Shape(int x,int y)
    {var t=Main.tile[x,y];return $"{t.HasTile}:{t.TileType}:{t.TileFrameX}:{t.TileFrameY}:{t.Slope}:{t.IsHalfBlock}:{t.IsActuated}";}
    internal static long Revision(int x,int y)=>revisions.TryGetValue((x,y),out long value)?value:long.MinValue;
    internal static void Watch(int x,int y)
    {if(!revisions.ContainsKey((x,y))){if(revisions.Count>=512)revisions.Remove(revisions.Keys.First());revisions[(x,y)]=++sequence;}}
    internal static void Changed(int x,int y){if(revisions.ContainsKey((x,y)))revisions[(x,y)]=++sequence;}
    internal static Target Observe(AiReadOnlyProtocol.Identity id,int x,int y)
    {
        Scope(id);
        foreach(var key in entries.Where(e=>e.Value.Expires<Environment.TickCount64).Select(e=>e.Key).ToArray())entries.Remove(key);
        if(entries.Count>=256)entries.Remove(entries.Keys.First());
        Watch(x,y);
        var token=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");
        var target=new Target("tile",x,y,Main.tile[x,y].TileType,id.World,id.Generation,id.Slot,Main.GameUpdateCount,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),token);
        entries[token]=new(target,id.Actor,Environment.TickCount64+30000,Shape(x,y),Revision(x,y));return target;
    }
    internal static string Resolve(AiReadOnlyProtocol.Identity id,JsonElement args,out int x,out int y)
    {
        Scope(id);x=y=0;
        if(!args.TryGetProperty("target",out var supplied)){x=args.GetProperty("x").GetInt32();y=args.GetProperty("y").GetInt32();return null;}
        string token=supplied.GetProperty("targetToken").GetString();
        if(!entries.TryGetValue(token,out var e))return "observed_target_unknown_or_evicted";
        if(!ReferenceEquals(e.Actor,id.Actor)||e.Target.worldSessionId!=id.World||e.Target.actorGeneration!=id.Generation||e.Target.actorSlot!=id.Slot)return "observed_target_binding_changed";
        if(Environment.TickCount64>e.Expires||Main.GameUpdateCount<e.Target.observedGameUpdate||Main.GameUpdateCount-e.Target.observedGameUpdate>600)return "observed_target_expired";
        var expected=JsonSerializer.SerializeToElement(e.Target);
        if(supplied.EnumerateObject().Count()!=expected.EnumerateObject().Count()||expected.EnumerateObject().Any(p=>!supplied.TryGetProperty(p.Name,out var v)||v.GetRawText()!=p.Value.GetRawText()))return "observed_target_modified";
        x=e.Target.x;y=e.Target.y;
        if(Revision(x,y)!=e.Revision||Shape(x,y)!=e.Shape)return "target_changed_since_observation";
        return null;
    }
}
