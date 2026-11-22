using System;
using CompanionBridge.ServerIpc;
using Microsoft.Xna.Framework;
using MonoMod.Cil;
using Terraria.GameContent.Achievements;
using Terraria;

namespace VirtualPlayerEngineProbe;

// One natural wrapper call owns the virtual cursor; action modules own the
// selected item, press cadence and evidence collected around that call.
internal static class NativeItemInput
{
    internal readonly record struct Intent(Vector2 Aim,bool Press,bool Alternate=false)
    {
        internal static Intent Untargeted(Player p,bool press)=>new(p.Center+new Vector2(p.direction*400f,0),press);
        internal static Intent AtTile(int x,int y,bool press)=>new(new Vector2(x*16+8,y*16+8),press);
    }

    private static readonly Action<Player> manageRightClick=typeof(Player).GetMethod("ItemCheck_ManageRightClickFeatures",
        System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public).CreateDelegate<Action<Player>>();
    [ThreadStatic] private static Player actor;
    internal static bool Active(Player p)=>actor!=null&&ReferenceEquals(actor,p);
    public static bool SkipClientUi(Player p)=>Active(p);

    internal static void Install(Action<Action,Action> install)
    {
        ILContext.Manipulator wrapper=il=>ItemUsePatches.Patch(il.Method,"wrapper",
            il.Module.ImportReference(typeof(NativeItemInput).GetMethod(nameof(SkipClientUi))));
        install(()=>IL_Player.ItemCheckWrapped+=wrapper,()=>IL_Player.ItemCheckWrapped-=wrapper);
        install(()=>On_Player.ItemCheckWrapped+=Update,()=>On_Player.ItemCheckWrapped-=Update);
        ILContext.Manipulator yoyoAchievement=il=>
        {
            var cursor=new ILCursor(il);
            cursor.GotoNext(MoveType.Before,i=>i.MatchLdarg(0),i=>i.MatchLdcI4(7),
                i=>i.MatchCall(typeof(AchievementsHelper),"HandleSpecialEvent"));
            cursor.Index+=2;
            cursor.Remove();
            cursor.EmitDelegate<Action<Player,int>>((owner,eventId)=>{
                if(!Active(owner)) AchievementsHelper.HandleSpecialEvent(owner,eventId);
            });
        };
        install(()=>IL_Player.ItemCheck_Shoot+=yoyoAchievement,()=>IL_Player.ItemCheck_Shoot-=yoyoAchievement);
        ServerCombatFixture.Install(install);
    }

    internal static void Update(On_Player.orig_ItemCheckWrapped orig,Player p,int index)
    {
        // Native item callbacks can synchronously enter this hook for any Player.
        // No nested original may run while the outer call owns the global cursor.
        if(actor!=null)throw new InvalidOperationException("Reentrant native item update during an AI input scope.");
        if(!ReferenceEquals(p,ProbeSession.Actor)){orig(p,index);return;}
        if(!NativeSurvival.Owns(p))return;
        if(index!=p.whoAmI)throw new InvalidOperationException("Native item update slot differs from bound actor.");
        void RunFrame(Intent input)=>Run(orig,p,index,input);
        if(NativeCombatWorld.UpdateItem(p,RunFrame))return;
        if(ActorMining.Running)ActorMining.Update(p,RunFrame);
        else ActorItemUse.Update(p,RunFrame);
    }

    internal static void Run(On_Player.orig_ItemCheckWrapped orig,Player p,int index,Intent input)
    {
        if(actor!=null||!NativeSurvival.Owns(p)||!ActorItemUse.Allowed(p))
            throw new InvalidOperationException("Native item input has no exclusive Actor scope.");

        int local=Main.myPlayer;
        var mouse=(Main.mouseX,Main.mouseY,Main.lastMouseX,Main.lastMouseY);
        var buttons=(Main.mouseLeft,Main.mouseLeftRelease,Main.mouseRight,Main.mouseRightRelease);
        var target=(Player.tileTargetX,Player.tileTargetY);
        var screen=Main.screenPosition;
        var mouseItem=Main.mouseItem;
        bool useTile=p.controlUseTile;
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
            p.controlUseItem=input.Press && !input.Alternate;
            Main.mouseLeft=p.controlUseItem;Main.mouseLeftRelease=p.releaseUseItem;
            Main.mouseRight=false;Main.mouseRightRelease=true;
            if(input.Alternate)
            {
                p.controlUseTile=Main.mouseRight=input.Press;
                Main.mouseRightRelease=p.releaseUseItem;
                manageRightClick(p);
                if(input.Press && p.itemTime==0 && p.itemAnimation==0 && p.altFunctionUse==0)
                {
                    p.controlUseItem=false;
                    NativeCombatWorld.NoAlternateInput(p);
                }
                p.controlUseTile=false;
            }
            orig(p,index);
        }
        finally
        {
            p.controlUseTile=useTile;
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
