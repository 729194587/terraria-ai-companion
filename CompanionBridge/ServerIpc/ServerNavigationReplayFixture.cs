using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Terraria;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Prepared only by the isolated copied-world terrain fixture. A normal bound
// move_input action owns every update, including release and cancellation.
internal static class ServerNavigationReplayFixture
{
    private static int[] inputs;
    private static int index;
    private static readonly List<float[]> positions=new();
    private static ulong firstTick,lastTick;
    internal static void Prepare(JsonElement request)
    {
        inputs=null;index=0;positions.Clear();
        if(!request.TryGetProperty("replay",out var sequence))return;
        if(sequence.GetArrayLength() is <1 or >600)throw new ArgumentException("Replay length");
        var keys=sequence.EnumerateArray().Select(v=>v.GetInt32()).ToArray();
        if(keys.Any(i=>i is <0 or >5))throw new ArgumentException("Replay key");
        inputs=keys;
    }
    internal static bool Input(Player player)
    {
        if(inputs==null)return false;
        if(index==0)firstTick=Main.GameUpdateCount;
        if(index>=inputs.Length || index>0 && Main.GameUpdateCount!=lastTick+1)
            throw new InvalidOperationException("Nonconsecutive native replay");
        lastTick=Main.GameUpdateCount;
        positions.Add(new[]{player.position.X,player.position.Y});
        int key=inputs[index++];
        player.controlLeft=key%3==0;player.controlRight=key%3==2;player.controlJump=key>=3;
        return true;
    }
    internal static void Finish()
    {
        if(inputs==null)return;
        inputs=null;
        var p=ProbeSession.Actor;
        var end=ServerAiNavigation.Observe(p);
        positions.Add(new[]{p.position.X,p.position.Y});
        var evidence=new{firstTick,lastTick,frames=index,positions,end.SafeSupport,end.Settled,end.State.Grounded,
            p.dead,p.statLife,world=ProbeSession.WorldSession,generation=ProbeSession.ActorGeneration,slot=p.whoAmI,
            released=!p.controlLeft&&!p.controlRight&&!p.controlJump&&!p.controlDown&&!p.controlUp};
        string path=Path.Combine(Main.SavePath,"navigation-replay.json");
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(evidence));File.Move(path+".tmp",path,true);
    }
}
