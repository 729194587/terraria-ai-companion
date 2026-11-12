using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Accessed only after a real Main.Update in the marked, no-client diagnostic.
internal static class ServerBenchmarkFixture
{
    private sealed record Cell(int X,int Y);
    private sealed record Proof(int X,int Y,int Type,ulong Tick);
    private static string run,scenario;
    private static JsonElement binding;
    private static Cell[] targets;
    private static Cell[] guards;
    private static string initialGuard;
    private static Vector2 start;
    private static int life,settle,left,floor;
    private static JsonElement pending;
    private static bool ready,evaluated;
    private static float stanceDelta;
    private static ActorMining.Context initialMining;
    private static ServerAiNavigation.Run initialNavigation;
    private static readonly Dictionary<Guid,object> mining=new();
    private static readonly Dictionary<(Guid,int,int),Proof> proofs=new();
    private static readonly Dictionary<ServerAiNavigation.Run,object> navigation=new();
    internal static bool Enabled=>ServerAiActionFixture.Enabled
        &&Program.LaunchParameters.ContainsKey("-companionbenchmarktest")
        &&File.Exists(Path.Combine(Main.SavePath,"isolated-benchmark-fixture"));

    internal static void Tick(Player player)
    {
        if(!Enabled||Netplay.HasClients||!Program.IsMainThread)return;
        NetworkStateSync.Require(player);
        if(ready&&!evaluated)Observe(player);
        if(settle>0)
        {
            if(--settle==0)Respond(pending,()=>Initial(player));
            return;
        }
        var path=Path.Combine(Main.SavePath,"benchmark-request.json");
        if(!File.Exists(path))return;
        string body;
        try {body=File.ReadAllText(path);File.Delete(path);}
        catch(IOException){return;}
        using var doc=JsonDocument.Parse(body);
        var request=doc.RootElement;
        if(request.GetProperty("phase").GetString()=="setup")
        {
            try { Setup(player,request);pending=request.Clone();settle=6; }
            catch(Exception error){Respond(request,()=>throw error);}
        }
        else Respond(request,()=>Final(player,request));
    }

    private static void Respond(JsonElement request,Func<object> operation)
    {
        object evidence=null;
        string error=null;
        try{evidence=operation();}catch(Exception e){error=e.Message;}
        var output=Path.Combine(Main.SavePath,"benchmark-response.json");
        File.WriteAllText(output+".tmp",JsonSerializer.Serialize(new{
            run=request.GetProperty("run"),scenario=request.GetProperty("scenario"),phase=request.GetProperty("phase"),
            nonce=request.GetProperty("nonce"),binding=request.GetProperty("binding"),error,evidence}));
        File.Move(output+".tmp",output,true);
    }

    private static void RequireBinding(Player p,JsonElement id)
    {
        if(id.GetProperty("world").GetString()!=ProbeSession.WorldSession
            ||id.GetProperty("generation").GetInt64()!=ProbeSession.ActorGeneration
            ||id.GetProperty("slot").GetInt32()!=p.whoAmI||!ReferenceEquals(p,ProbeSession.Actor))
            throw new InvalidOperationException("Benchmark actor binding changed.");
    }

    private static void Setup(Player p,JsonElement request)
    {
        if(run!=null||ActorControl.Running||ActorMining.Running||ActorItemUse.Running)
            throw new InvalidOperationException("A benchmark profile accepts one setup.");
        binding=request.GetProperty("binding").Clone();
        RequireBinding(p,binding);
        run=request.GetProperty("run").GetString();
        scenario=request.GetProperty("scenario").GetString();
        var setup=request.GetProperty("setup");
        left=Main.spawnTileX;floor=Main.spawnTileY+8;
        var box=setup.GetProperty("arena").EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        if(box.Length!=4||box[0]<-32||box[1]<-32||box[2]>32||box[3]>16
            ||box[0]>=0||box[1]>=-4||box[2]<=2||box[3]<=0
            ||!WorldGen.InWorld(left+box[0],floor+box[1],2)||!WorldGen.InWorld(left+box[2],floor+box[3],2))
            throw new InvalidOperationException("Invalid benchmark arena.");
        targets=setup.GetProperty("targets").EnumerateArray().Select(point=>new Cell(left+point[0].GetInt32(),floor+point[1].GetInt32())).ToArray();
        if(targets.Length is <1 or >18||targets.Distinct().Count()!=targets.Length||targets.Any(c=>
            c.X<=left+box[0]||c.X>=left+box[2]||c.Y<=floor+box[1]||c.Y>=floor))
            throw new InvalidOperationException("Invalid benchmark targets.");
        ushort ground=setup.GetProperty("floorType").GetUInt16(), ore=setup.GetProperty("targetType").GetUInt16();
        if(ground!=TileID.Stone||ore!=TileID.Copper||setup.GetProperty("itemType").GetInt32()!=ItemID.CopperPickaxe
            ||setup.GetProperty("selectedSlot").GetInt32()!=0)throw new InvalidOperationException("Unsupported Phase 1 setup.");
        var targetSet=targets.ToHashSet();
        var guardList=new List<Cell>();
        for(int x=left+box[0];x<=left+box[2];x++)
        for(int y=floor+box[1];y<=floor+box[3];y++)
        {
            var cell=new Cell(x,y);var tile=Main.tile[x,y];tile.ClearEverything();
            if(targetSet.Contains(cell)){tile.HasTile=true;tile.TileType=ore;}
            else
            {
                if(y>=floor||y==floor+box[1]){tile.HasTile=true;tile.TileType=ground;}
                guardList.Add(cell);
            }
        }
        guards=guardList.ToArray();
        foreach(var item in Main.item)item.active=false;
        Array.Clear(p.buffType);Array.Clear(p.buffTime);
        foreach(var items in new[]{p.inventory,p.armor,p.dye,p.miscEquips,p.miscDyes})
            for(int i=0;i<items.Length;i++)items[i]=new Item();
        p.inventory[0]=new Item(ItemID.CopperPickaxe);p.selectedItem=0;
        p.position=new(left*16,floor*16-p.height);p.velocity=Vector2.Zero;
        p.fallStart=floor;p.jump=0;p.releaseJump=true;p.statLife=p.statLifeMax2;
        initialMining=ActorMining.Current;initialNavigation=(ActorControl.Driver as ServerAiNavigation)?.Current;
        initialGuard=GuardDigest();
        NetworkStateSync.Full(p);
    }

    private static string Digest(object value)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static string GuardDigest()=>Digest(guards.Select(c=>{
        var t=Main.tile[c.X,c.Y];
        return new{t.HasTile,type=t.HasTile?t.TileType:0,t.Slope,t.IsHalfBlock,t.IsActuated,t.WallType,t.LiquidAmount,t.LiquidType,
            t.RedWire,t.BlueWire,t.GreenWire,t.YellowWire,t.HasActuator};
    }));
    private static object[] TargetStates()=>targets.Select(c=>{
        var t=Main.tile[c.X,c.Y];
        return (object)new{x=c.X,y=c.Y,present=t.HasTile,type=t.HasTile?(int?)t.TileType:null};
    }).ToArray();
    private static bool Released(Player p)=>!ActorControl.Running&&!ActorMining.Running&&!ActorItemUse.Running
        &&!p.controlLeft&&!p.controlRight&&!p.controlUp&&!p.controlDown&&!p.controlJump&&!p.controlUseItem&&!p.controlUseTile
        &&!p.channel&&p.itemTime==0&&p.itemAnimation==0&&p.reuseDelay==0;
    private static object Initial(Player p)
    {
        RequireBinding(p,binding);
        var pose=ServerAiNavigation.Observe(p);
        if(!pose.Settled||!Released(p)||p.dead||p.ghost||p.selectedItem!=0||p.HeldItem.type!=ItemID.CopperPickaxe
            ||p.inventory.Skip(1).Any(i=>!i.IsAir)||p.armor.Any(i=>!i.IsAir)||p.buffType.Any(b=>b!=0)
            ||initialGuard!=GuardDigest()
            ||targets.Any(c=>!Main.tile[c.X,c.Y].HasUnactuatedTile||Main.tile[c.X,c.Y].TileType!=TileID.Copper))
            throw new InvalidOperationException("Benchmark did not settle with its initial guards and targets intact.");
        start=p.position;life=p.statLife;
        var position=new{x=start.X,y=start.Y};
        var layout=new{guardDigest=initialGuard,guardedCells=guards.Length,targets=TargetStates(),position,life,p.selectedItem,
            origin=new{x=left,y=floor},inventory=p.inventory.Select(i=>new{i.type,i.stack,i.prefix}).ToArray(),
            armor=p.armor.Select(i=>new{i.type,i.prefix}).ToArray(),buffs=p.buffType.Where(b=>b!=0).ToArray()};
        int reachable=targets.Count(c=>ActorMining.QueryReach(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,p.HeldItem,c.X,c.Y,start.X,start.Y));
        ready=true;
        return new{gameUpdate=Main.GameUpdateCount,layout,fingerprint=Digest(layout),reachable,objective=false,guardsPassed=true,
            modVersion=ModContent.GetInstance<global::CompanionBridge.CompanionBridge>().Version.ToString()};
    }

    private static void Observe(Player p)
    {
        if(ActorMining.Current is {} mine&&!ReferenceEquals(mine,initialMining)&&ReferenceEquals(mine.Actor,p))
        {
            foreach(var proof in mine.Batch?.Verified.ToArray()??Array.Empty<ExcavationProof>())
                proofs.TryAdd((mine.Id,proof.X,proof.Y),new(proof.X,proof.Y,proof.Type,Main.GameUpdateCount));
            if(mine.Batch==null&&mine.Destroyed&&!mine.ExternalChange&&!Main.tile[mine.X,mine.Y].HasTile)
                proofs.TryAdd((mine.Id,mine.X,mine.Y),new(mine.X,mine.Y,mine.TileType,Main.GameUpdateCount));
            mining[mine.Id]=new{id=mine.Id,status=mine.Phase.ToString(),mine.Ticks,mine.Strikes};
        }
        if((ActorControl.Driver as ServerAiNavigation)?.Current is {} nav&&!ReferenceEquals(nav,initialNavigation)
            &&ReferenceEquals(nav.Identity.Actor,p))
            navigation[nav]=new{status=nav.Status,nav.Updates,tick=nav.FinalObservationTick,nav.StartX,
                endX=nav.FinalObservation?.State.Px,settled=nav.FinalObservation?.Settled==true};
        if(Math.Abs(p.velocity.X)<.1f&&Math.Abs(p.velocity.Y)<.1f)
            stanceDelta=Math.Max(stanceDelta,Vector2.Distance(start,p.position));
    }

    private static object Final(Player p,JsonElement request)
    {
        RequireBinding(p,request.GetProperty("binding"));
        if(!ready||evaluated||request.GetProperty("phase").GetString()!="evaluate"
            ||request.GetProperty("run").GetString()!=run||request.GetProperty("scenario").GetString()!=scenario
            ||request.GetProperty("binding").GetRawText()!=binding.GetRawText())
            throw new InvalidOperationException("Evaluation does not match the ready benchmark.");
        evaluated=true;
        return new{gameUpdate=Main.GameUpdateCount,targets=TargetStates(),guardDigest=GuardDigest(),
            alive=!p.dead&&!p.ghost,health=p.statLife,healthUnchanged=p.statLife==life,released=Released(p),stanceDelta,
            position=new{x=p.position.X,y=p.position.Y},
            mining=mining.Values.ToArray(),nativeProof=proofs.Select(pair=>new{runId=pair.Key.Item1,
                x=pair.Value.X,y=pair.Value.Y,type=pair.Value.Type,tick=pair.Value.Tick}).ToArray(),
            navigation=navigation.Values.ToArray()};
    }
}
