using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal sealed partial class ExcavationTask
{
    private NativeNavigationEdits corridor;
    private TerrainApproachPlanner planner;
    private TerrainRouteExecution route;
    private int changesSinceOre,navigationDispatches,navigationFailures,actualCuts,actualPlaces,replans;
    private double planningMs,maxSliceMs;
    private readonly Dictionary<string,ulong> updates=new();
    private readonly Queue<object> decisions=new();
    private static string Category(string phase)=>phase switch
    {
        "planning"=>"planning","approaching" or "navigating"=>"movement","clearing" or "placing"=>"clearance",
        "mining" or "opening"=>"ore",_=>"waiting"
    };
    private object ApproachEvidence()=>new{decisions=decisions.ToArray(),navigationDispatches=navigationDispatches+(route?.Moves??0),
        navigationFailures,actualCuts=actualCuts+(route?.Cuts??0),actualPlaces=actualPlaces+(route?.Places??0),replans,planningMs,maxSliceMs,updates};

    private void CollectRoute()
    {
        changesSinceOre+=route.Cuts+route.Places;
        actualCuts+=route.Cuts;actualPlaces+=route.Places;navigationDispatches+=route.Moves;
        navigationFailures+=route.MoveFailures;route=null;
    }

    internal static bool IsOre(AiReadOnlyProtocol.Identity identity,JsonElement args)=>
        SceneMineTargets.Resolve(identity,args,out int x,out int y)==null&&WorldGen.InWorld(x,y,2)
        &&Main.tile[x,y].HasUnactuatedTile&&TileID.Sets.Ore[Main.tile[x,y].TileType]
        &&!Main.tileFrameImportant[Main.tile[x,y].TileType];

    private bool TrySupport(ExcavationCell cell)
    {
        if(!singleOre)
        {
            directTarget=new NativeMineTargetWorld(identity,JsonSerializer.SerializeToElement(new{x=cell.X,y=cell.Y}));
            if(directTarget.Prepare()!=null)return false;
        }
        if(!directTarget.CanMine())return false;
        StartSingle(cell);return true;
    }

}
