using System;
using CompanionBridge.Motion;
using Terraria;

// Minimal game boundary for the MobilityOnly compilation.
namespace Terraria
{
    public sealed class Player
    {
        public bool controlLeft,controlRight,controlJump,controlDown,controlUp,controlUseItem,controlUseTile;
    }
}
namespace Terraria.ModLoader
{
    internal static class ModContent { public static T GetInstance<T>() where T:new()=>new(); }
}
namespace CompanionBridge
{
    internal sealed class CompanionBridge
    {
        internal CompanionBridge Logger=>this;
        public void Warn(string text) { }
    }
}

public static class AirActionFaultTests
{
    private sealed class Adapter(string key,bool failCapture):INavigationAirAction
    {
        public string Key=>key;
        internal int Captures,Activations;
        public NavigationAirImpulse Capture(Player player,out int cooldown)
        {
            Captures++;
            if(failCapture)throw new InvalidOperationException();
            cooldown=0;
            return new(Key,8,-3,10);
        }
        public void Activate(Player player,int direction)
        {
            Activations++;
            throw new InvalidOperationException();
        }
    }
    public static void Run()
    {
        var p=new Player{controlLeft=true,controlRight=true,controlJump=true,controlDown=true,
            controlUp=true,controlUseItem=true,controlUseTile=true};
        var capture=new Adapter("capture",true);
        var activate=new Adapter("activate",false);
        NavigationAirActions.Register(capture);
        NavigationAirActions.Register(activate);
        try
        {
            if(NavigationAirActions.Capture(p).Action?.Key!="activate")throw new Exception("healthy adapter lost");
            if(NavigationAirActions.Activate(p,new(1,true,ImpulseKey:"activate")))throw new Exception("failed activation accepted");
            if(p.controlLeft||p.controlRight||p.controlJump||p.controlDown||p.controlUp||p.controlUseItem||p.controlUseTile)
                throw new Exception("input retained after adapter failure");
            if(NavigationAirActions.Capture(p).Action!=null || capture.Captures!=1 || activate.Captures!=2 || activate.Activations!=1)
                throw new Exception("faulted adapter called again");
        }
        finally { NavigationAirActions.Reset(); }
        Console.WriteLine("Air action exceptions: capture isolation, quarantine and input release passed.");
    }
}
