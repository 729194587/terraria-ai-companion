using System;
using System.Reflection;
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
