using System;
using CompanionBridge;

public static class LiquidTerrainTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
        long previous=0;
        foreach(byte amount in new byte[]{1,128,255})
        {
            var water=new TileLiquid(0,amount);
            var platform=new TerrainCell('=',water);
            Check(platform.Shape=='='&&platform.Liquid.Kind==LiquidKind.Water
                &&platform.Liquid.Amount==amount,"liquid amount and structural platform coexist");
            Check(water.Stamp!=previous,"liquid amount participates in the cell stamp");
            previous=water.Stamp;
        }
        Check(new TileLiquid(3,0)==new TileLiquid(0,0),"empty cells ignore stale liquid type");
        Check(new TileLiquid(3,128).Stamp!=new TileLiquid(0,128).Stamp,"nonempty type participates in the stamp");
        Check(new TileLiquid(99,1).Kind==LiquidKind.Unknown,"unrecognized nonempty type is unknown");
        Check(new TileLiquid(99,1).Stamp!=new TileLiquid(98,1).Stamp,"unknown type changes invalidate the stamp");
        Check(new TerrainCell('?',new(0,255)).DryShape=='?',"legacy dry projection preserves unknown");
        Check(new TerrainCell('.',new(0,1)).DryShape=='!',"legacy dry projection rejects liquid");
        var half=new TileLiquid(2,128);
        Check(!half.OverlapsVertical(10,150,168)&&half.OverlapsVertical(10,150,168.01f),
            "half-filled surface uses a strict lower-body overlap");
        Check(!half.OverlapsVertical(10,176,186),"body entirely below this liquid cell has no overlap");
        Console.WriteLine($"Phase 4.6 liquid terrain: {checks} checks");
    }
}
