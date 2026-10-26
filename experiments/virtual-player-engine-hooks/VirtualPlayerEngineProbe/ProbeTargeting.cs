using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Chat;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;

namespace VirtualPlayerEngineProbe;

// Client-only, read-only targeting. Submit the existing native World command;
// the server remains the sole authority for identity, permission and mining.
internal static class ProbeTargeting
{
    internal static bool Enabled,HasTarget;
    internal static Point Target;
    private static Player viewer,watched;
    private static int lastAnimation;
    private static bool lastUse;
    private static bool Client=>!Main.dedServ&&!Main.gameMenu&&Main.netMode==NetmodeID.MultiplayerClient&&!Netplay.Disconnect;
    internal static void Reset(){Enabled=HasTarget=false;Target=default;viewer=watched=null;lastAnimation=0;lastUse=false;}
    internal static void Toggle()
    {Enabled=!Enabled;HasTarget=false;viewer=null;}
    internal static void Capture(Player p)
    {
        if(!Enabled||!Client||!ReferenceEquals(p,Main.LocalPlayer)||!p.active||p.dead||!Main.hasFocus)return;
        // PreItemCheck has the engine's world/zoom-correct mouse coordinates.
        // Opening chat/inventory freezes the last world target, never the UI.
        if(Main.drawingPlayerChat||Main.playerInventory||Main.mapFullscreen||p.mouseInterface)return;
        var world=Main.MouseWorld;
        if(!float.IsFinite(world.X)||!float.IsFinite(world.Y)){HasTarget=false;return;}
        var tile=WorldTile(world);int x=tile.X,y=tile.Y;
        HasTarget=WorldGen.InWorld(x,y,2);
        if(HasTarget){Target=new Point(x,y);viewer=p;}
    }
    internal static Point WorldTile(Vector2 world)=>new((int)Math.Floor(world.X/16),(int)Math.Floor(world.Y/16));
    internal static Rectangle TargetRectangle(Point tile,Vector2 screen,Matrix transform,float uiScale)
    {
        var a=Vector2.Transform(new Vector2(tile.X*16,tile.Y*16)-screen,transform)/uiScale;
        var b=Vector2.Transform(new Vector2((tile.X+1)*16,(tile.Y+1)*16)-screen,transform)/uiScale;
        return new Rectangle((int)Math.Min(a.X,b.X),(int)Math.Min(a.Y,b.Y),Math.Max(1,(int)Math.Abs(b.X-a.X)),Math.Max(1,(int)Math.Abs(b.Y-a.Y)));
    }
    internal static void Submit(CommandCaller caller)
    {
        if(!Client||!Enabled||!HasTarget||!ReferenceEquals(viewer,Main.LocalPlayer)||viewer.dead)
        {caller.Reply("先输入 /vptarget，关闭聊天并把鼠标指向方块，再打开聊天输入 /vpmine。",Color.Yellow);return;}
        if(!WorldGen.InWorld(Target.X,Target.Y,2)||!Main.tile[Target.X,Target.Y].HasTile)
        {caller.Reply("选中位置没有方块；关闭聊天后重新指向目标。",Color.Yellow);return;}
        string command=$"/vph mine {Target.X} {Target.Y}";
        ChatHelper.SendChatMessageFromClient(new ChatMessage(command));
        caller.Reply($"已提交挖掘 Tile ({Target.X},{Target.Y})，等待服务器校验；/vph mine-status 查看结果。",Color.LightCyan);
    }
    internal static bool Draw()
    {
        if(!Enabled||!Client)return true;
        string text="挖掘选框：移动鼠标指向方块，再打开聊天输入 /vpmine；/vptarget 关闭";
        if(HasTarget&&ReferenceEquals(viewer,Main.LocalPlayer)&&WorldGen.InWorld(Target.X,Target.Y,2))
        {
            var tile=Main.tile[Target.X,Target.Y];
            text=$"挖掘目标 Tile ({Target.X},{Target.Y})，"+(tile.HasTile?$"方块类型 {tile.TileType}":"空格")
                +"\n打开聊天后目标冻结；/vpmine 挖这一格，/vph mine-stop 停止";
            var box=TargetRectangle(Target,Main.screenPosition,Main.GameViewMatrix.TransformationMatrix,Main.UIScale);
            int x=box.X,y=box.Y,w=box.Width,h=box.Height;
            var pixel=TextureAssets.MagicPixel.Value;var color=tile.HasTile?Color.Yellow:Color.OrangeRed;
            Main.spriteBatch.Draw(pixel,new Rectangle(x,y,w,2),color);Main.spriteBatch.Draw(pixel,new Rectangle(x,y+h-2,w,2),color);
            Main.spriteBatch.Draw(pixel,new Rectangle(x,y,2,h),color);Main.spriteBatch.Draw(pixel,new Rectangle(x+w-2,y,2,h),color);
        }
        Utils.DrawBorderString(Main.spriteBatch,text,new Vector2(24,Main.screenHeight/Main.UIScale-150),Color.LightCyan,0.8f);
        return true;
    }
    internal static void Watch(Player p){watched=p;lastAnimation=p.itemAnimation;lastUse=p.controlUseItem;}
    internal static void Observe(Player p)
    {
        if(!Client||!ReferenceEquals(p,watched))return;
        if(p.itemAnimation>lastAnimation||lastAnimation>0&&p.itemAnimation==0||p.controlUseItem!=lastUse)
        {
            try{VirtualPlayerEngineProbe.Instance?.Logger.Info(JsonSerializer.Serialize(new{probe="VirtualPlayerEngineProbe",eventName="client-item-transition",
                slot=p.whoAmI,tick=Main.GameUpdateCount,animation=p.itemAnimation,time=p.itemTime,use=p.controlUseItem,selected=p.selectedItem,type=p.HeldItem.type}));}catch{}
        }
        lastAnimation=p.itemAnimation;lastUse=p.controlUseItem;
    }
}

public sealed class ProbeTargetingSystem : ModSystem
{
    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        if(Main.dedServ)return;
        int index=layers.FindIndex(layer=>layer.Name=="Vanilla: Mouse Text");if(index<0)index=layers.Count;
        layers.Insert(index,new LegacyGameInterfaceLayer("VirtualPlayerEngineProbe: Tile target",ProbeTargeting.Draw,InterfaceScaleType.UI));
    }
}

public sealed class ProbeTargetCommand : ModCommand
{
    public override string Command=>"vptarget";
    public override CommandType Type=>CommandType.Chat;
    public override string Usage=>"/vptarget";
    public override string Description=>"显示鼠标所指方块的选框和世界 Tile 坐标；打开聊天后冻结目标。";
    public override void Action(CommandCaller caller,string input,string[] args)
    {
        if(args.Length!=0){caller.Reply(Usage);return;}
        ProbeTargeting.Toggle();caller.Reply(ProbeTargeting.Enabled?"挖掘选框已开启：关闭聊天，指向方块，再打开聊天输入 /vpmine。":"挖掘选框已关闭。",Color.LightCyan);
    }
}

public sealed class ProbeMouseMineCommand : ModCommand
{
    public override string Command=>"vpmine";
    public override CommandType Type=>CommandType.Chat;
    public override string Usage=>"/vpmine";
    public override string Description=>"向服务器提交选框中的一个方块，仍按 AI 的原生距离/工具规则执行。";
    public override void Action(CommandCaller caller,string input,string[] args)
    {if(args.Length!=0){caller.Reply(Usage);return;}ProbeTargeting.Submit(caller);}
}
