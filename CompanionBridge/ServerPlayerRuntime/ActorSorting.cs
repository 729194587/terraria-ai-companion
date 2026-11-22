using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.UI;
using CompanionBridge.ServerIpc;

namespace VirtualPlayerEngineProbe;

// Redirect the audited native call sites, without assigning a local player or changing UI state.
internal static class ActorSorting
{
    private static AiReadOnlyProtocol.Identity current;
    private static Action<Item[], int[]> sort;
    internal static bool Available => sort != null;
    private static MethodInfo Method(string name) =>
        typeof(ItemSorting).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(ItemSorting).FullName, name);

    private static Player PlayerForSort()
    {
        if (current == null) return Main.LocalPlayer;
        ServerAiNativeActions.Require(current);
        return (Player)current.Actor;
    }

    internal static void Install(Action<Action, Action> install)
    {
        if (VirtualPlayerEngineProbe.Engine.Selected != "2026.09.3.1") return;
        void Hook(string name, int reads, int getters, int glows, bool overflow = false)
        {
            ILHook hook = null;
            install(() => hook = new ILHook(Method(name), il => Patch(il, reads, getters, glows, overflow)),
                () => hook?.Dispose());
        }
        Hook("SetupSortingPriorities", 1, 0, 0);
        Hook("Sort", 1, 0, 1);
        Hook("SortInventory", 1, 1, 0);
        Hook("SortCoins", 0, 3, 0, overflow: true);
        Hook("ClearAmmoSlotSpaces", 1, 0, 0);
        Hook("FillAmmoFromInventory", 1, 0, 1);
        install(() => sort = Method("Sort").CreateDelegate<Action<Item[], int[]>>(), () => sort = null);
    }

    private static void Patch(ILContext il, int reads, int getters, int glows, bool overflow)
    {
        bool Field(Instruction i, string name) => i?.OpCode == OpCodes.Ldsfld
            && i.Operand is FieldReference field && field.DeclaringType.FullName == "Terraria.Main" && field.Name == name;
        bool Call(Instruction i, string type, string name) => i.Operand is MethodReference method
            && method.DeclaringType.FullName == type && method.Name == name;
        var instructions = il.Body.Instructions;
        var players = instructions.Where(i => Field(i, "player")).ToArray();
        var local = instructions.Where(i => Call(i, "Terraria.Main", "get_LocalPlayer")).ToArray();
        var glow = instructions.Where(i => Call(i, "Terraria.UI.ItemSlot", "SetGlow")).ToArray();
        var spawn = instructions.Where(i => Call(i, "Terraria.Player", "QuickSpawnItem")).ToArray();
        if (players.Length != reads || local.Length != getters || glow.Length != glows || spawn.Length != (overflow ? 1 : 0)
            || players.Any(i => !Field(i.Next, "myPlayer") || i.Next.Next.OpCode != OpCodes.Ldelem_Ref))
            throw new InvalidOperationException($"Unexpected native sorting context in {il.Method.Name}.");

        var cursor = new ILCursor(il);
        foreach (var read in players)
        {
            read.OpCode = read.Next.OpCode = read.Next.Next.OpCode = OpCodes.Nop;
            read.Operand = read.Next.Operand = read.Next.Next.Operand = null;
            cursor.Goto(read.Next.Next, MoveType.After);
            cursor.EmitDelegate<Func<Player>>(PlayerForSort);
        }
        foreach (var read in local.Concat(glow).Concat(spawn))
        {
            read.OpCode = OpCodes.Nop;
            read.Operand = null;
            cursor.Goto(read, MoveType.After);
            if (local.Contains(read)) cursor.EmitDelegate<Func<Player>>(PlayerForSort);
            else if (glow.Contains(read))
                cursor.EmitDelegate<Action<int, float, bool>>((slot, hue, chest) => {
                    if (current == null) ItemSlot.SetGlow(slot, hue, chest);
                });
            else cursor.EmitDelegate<Func<Player, Terraria.DataStructures.IEntitySource, int, int, int>>((player, source, type, stack) =>
                current == null ? player.QuickSpawnItem(source, type, stack)
                    : throw new InvalidOperationException("Native sort attempted coin overflow."));
        }
    }

    internal static void Run(AiReadOnlyProtocol.Identity identity, Item[] chest)
    {
        ServerAiNativeActions.Require(identity);
        if (!Available || current != null) throw new InvalidOperationException("Native sorting scope unavailable.");
        current = identity;
        try
        {
            if (chest == null) ItemSorting.SortInventory();
            else sort(chest, Array.Empty<int>());
        }
        finally { current = null; }
    }
}
