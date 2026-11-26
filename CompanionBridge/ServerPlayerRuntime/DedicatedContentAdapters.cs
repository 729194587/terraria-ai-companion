using System;
using System.Reflection;
using System.Linq;
using Microsoft.Xna.Framework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ModLoader;

namespace VirtualPlayerEngineProbe;

// Each dedicated-server adapter owns its subscriptions to the loaded content assembly.
internal static class DedicatedContentAdapters
{
    internal static void Install(Action<Action, Action> install)
    {
        if (!ModLoader.TryGetMod("RecipeBrowser", out var mod)) return;

        // Both OnPickup and OnCreated forward to this single UI notification.
        // No inventory, recipe or pickup hook is replaced. Preserve the original
        // notification whenever the UI exists, including on a graphical host.
        var type = mod.Code.GetType("RecipeBrowser.RecipeBrowserGlobalItem");
        var method = type?.GetMethod("ItemReceived", BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(Item) }, null);
        if (method == null) throw new MissingMethodException("RecipeBrowserGlobalItem.ItemReceived(Item)");
        ILHook hook = null;
        install(() => hook = new ILHook(method, GuardUiNotification), () => hook?.Dispose());
    }

    internal static void InstallVeinMining(Action<Action, Action> install)
    {
        if (!ModLoader.TryGetMod("ImproveGame", out var mod)) return;
        var packet = mod.Code.GetType("ImproveGame.Packets.Tiles.VeinMiningPacket", throwOnError: true);
        var receive = packet.GetMethod("Receive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
        if (receive?.ReturnType != typeof(void)) throw new MissingMethodException(packet.FullName, "Receive()");
        var system = mod.Code.GetType("ImproveGame.Content.Functions.VeinMiner.VeinMinerSystem", true);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var run = system.GetMethod("DoVeinMiningAt", flags).CreateDelegate<Action<Point,int>>();
        var breakOres = system.GetMethod("BreakAllOres", flags);
        ILHook cells = null, packetHook = null;
        install(() => cells = new ILHook(breakOres, GuardVeinCells), () => cells?.Dispose());
        install(() => packetHook = new ILHook(receive, il => BindVeinMiner(il, run)), () => packetHook?.Dispose());
    }

    private static void BindVeinMiner(ILContext il, Action<Point,int> run)
    {
        // NetSimplified reuses the packet instance. Resolve the actor at the point
        // ImproveGame consumes Sender, without modifying the shared packet or global player.
        var fields = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldfld).Select(i => (FieldReference)i.Operand);
        const string packet = "ImproveGame.Packets.Tiles.VeinMiningPacket::";
        var center = fields.Single(f => f.FullName == "Microsoft.Xna.Framework.Point " + packet + "_center");
        var tileType = fields.Single(f => f.FullName == "System.UInt16 " + packet + "_tileType");
        var store = il.Body.Instructions.Single(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f
            && f.FullName == "System.Int32 ImproveGame.Content.Functions.VeinMiner.VeinMinerSystem::MinerIndex"
            && i.Previous?.Operand is MethodReference getter && getter.FullName == "System.Int32 NetSimplified.NetModule::get_Sender()");
        var cursor = new ILCursor(il);
        cursor.Goto(store, MoveType.Before);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldfld, center);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldfld, tileType);
        cursor.EmitDelegate<Func<int, Point, int, int>>(ActorMining.VeinMinerSlot);
        var call = il.Body.Instructions.Single(i => i.MatchCall("ImproveGame.Content.Functions.VeinMiner.VeinMinerSystem","DoVeinMiningAt"));
        cursor.Goto(call, MoveType.Before);
        cursor.Remove();
        cursor.Emit(OpCodes.Ldsfld, (FieldReference)store.Operand);
        cursor.EmitDelegate<Action<Point,int,int>>((center, type, slot) => ActorMining.RunVeinPacket(center, type, slot, run));
    }

    private static void GuardVeinCells(ILContext il)
    {
        var kill = il.Body.Instructions.Single(i => i.MatchCall(typeof(WorldGen), nameof(WorldGen.KillTile)));
        var smooth = il.Body.Instructions.Single(i => i.MatchCall(typeof(Tile), nameof(Tile.SmoothSlope)));
        var cursor = new ILCursor(il);
        cursor.Goto(kill, MoveType.Before);
        cursor.Remove();
        cursor.EmitDelegate<Action<int,int,bool,bool,bool>>(ActorMining.BreakVeinCell);
        cursor.Goto(smooth, MoveType.Before);
        cursor.Remove();
        // ImproveGame smooths surrounding tiles after each cut. A mining task only owns its ore removals.
        cursor.EmitDelegate<Action<int,int,bool,bool>>((x,y,neighbors,sync) =>
        {
            if (!ActorMining.InAiVeinPacket) Tile.SmoothSlope(x,y,neighbors,sync);
        });
    }

    private static void GuardUiNotification(ILContext il)
    {
        var code = il.Body.Instructions;
        if (code.Count != 4 || code[0].OpCode != OpCodes.Ldsfld
            || code[0].Operand is not FieldReference ui || ui.Name != "instance"
            || ui.FieldType.FullName != "RecipeBrowser.RecipeBrowserUI"
            || code[1].OpCode != OpCodes.Ldarg_1 || code[2].OpCode != OpCodes.Callvirt
            || code[2].Operand is not MethodReference notification
            || notification.FullName != "System.Void RecipeBrowser.RecipeBrowserUI::ItemReceived(Terraria.Item)"
            || code[3].OpCode != OpCodes.Ret || il.Body.ExceptionHandlers.Count != 0)
            throw new InvalidOperationException("Recipe Browser UI notification shape changed.");
        var cursor = new ILCursor(il);
        cursor.Emit(OpCodes.Ldsfld, ui);
        cursor.Emit(OpCodes.Brfalse, code[code.Count - 1]);
    }
}
