using System;
using CompanionBridge.ServerIpc;
using Microsoft.Xna.Framework;
using MonoMod.Cil;
using Terraria;

namespace VirtualPlayerEngineProbe;

// One natural wrapper call owns the virtual cursor; action modules own the
// selected item, press cadence and evidence collected around that call.
internal static class NativeItemInput
{
    internal readonly record struct Intent(Vector2 Aim,bool Press)
    {
        internal static Intent Untargeted(Player p,bool press)=>new(p.Center+new Vector2(p.direction*400f,0),press);
        internal static Intent AtTile(int x,int y,bool press)=>new(new Vector2(x*16+8,y*16+8),press);
    }

    [ThreadStatic] private static Player actor;
    internal static bool Active(Player p)=>actor!=null&&ReferenceEquals(actor,p);
    public static bool SkipClientUi(Player p)=>Active(p);

    internal static void Install(Action<Action,Action> install)
    {
        ILContext.Manipulator wrapper=il=>ItemUsePatches.Patch(il.Method,"wrapper",
            il.Module.ImportReference(typeof(NativeItemInput).GetMethod(nameof(SkipClientUi))));
        install(()=>IL_Player.ItemCheckWrapped+=wrapper,()=>IL_Player.ItemCheckWrapped-=wrapper);
        install(()=>On_Player.ItemCheckWrapped+=Update,()=>On_Player.ItemCheckWrapped-=Update);
    }

    internal static void Update(On_Player.orig_ItemCheckWrapped orig,Player p,int index)
    {
        if(!ReferenceEquals(p,ProbeSession.Actor)){orig(p,index);return;}
        if(!NativeSurvival.Owns(p))return;
        if(actor!=null||index!=p.whoAmI)throw new InvalidOperationException("Invalid or reentrant AI item update.");
        // Mining and combat keep their existing execution routes.
        if(NativeCombatWorld.UpdateItem(p))return;
        if(ActorMining.Running){ActorMining.Update(p);return;}
        ActorItemUse.Update(p,input=>Run(orig,p,index,input));
    }

    private static void Run(On_Player.orig_ItemCheckWrapped orig,Player p,int index,Intent input)
    {
        if(actor!=null||!NativeSurvival.Owns(p)||!ActorItemUse.Allowed(p))
            throw new InvalidOperationException("Native item input has no exclusive Actor scope.");

        int local=Main.myPlayer;
        var mouse=(Main.mouseX,Main.mouseY,Main.lastMouseX,Main.lastMouseY);
        var buttons=(Main.mouseLeft,Main.mouseLeftRelease,Main.mouseRight,Main.mouseRightRelease);
        var target=(Player.tileTargetX,Player.tileTargetY);
        var screen=Main.screenPosition;
        var mouseItem=Main.mouseItem;
        bool blockMouse=Main.blockMouse,mouseInterface=p.mouseInterface;
        try
        {
            actor=p;
            Main.myPlayer=index;
            var origin=p.Center-new Vector2(Main.screenWidth,Main.screenHeight)*.5f;
            Main.screenPosition=new Vector2(MathF.Floor(origin.X),MathF.Floor(origin.Y));
            Main.mouseX=Main.lastMouseX=(int)(input.Aim.X-Main.screenPosition.X);
            Main.mouseY=Main.lastMouseY=(int)(input.Aim.Y-Main.screenPosition.Y);
            Player.tileTargetX=Math.Clamp((int)(input.Aim.X/16f),1,Main.maxTilesX-2);
            Player.tileTargetY=Math.Clamp((int)(input.Aim.Y/16f),1,Main.maxTilesY-2);
            Main.mouseItem=new Item();
            Main.blockMouse=p.mouseInterface=false;
            p.controlUseItem=input.Press;
            Main.mouseLeft=input.Press;Main.mouseLeftRelease=p.releaseUseItem;
            Main.mouseRight=false;Main.mouseRightRelease=true;
            orig(p,index);
        }
        finally
        {
            (Player.tileTargetX,Player.tileTargetY)=target;
            (Main.mouseX,Main.mouseY,Main.lastMouseX,Main.lastMouseY)=mouse;
            (Main.mouseLeft,Main.mouseLeftRelease,Main.mouseRight,Main.mouseRightRelease)=buttons;
            Main.screenPosition=screen;
            Main.mouseItem=mouseItem;Main.blockMouse=blockMouse;p.mouseInterface=mouseInterface;
            Main.myPlayer=local;
            actor=null;
        }
    }
}
