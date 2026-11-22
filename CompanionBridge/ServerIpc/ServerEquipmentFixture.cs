using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal static class ServerEquipmentFixture
{
    internal static bool Enabled => Program.LaunchParameters.ContainsKey("-companionequipmenttest")
        && Program.LaunchParameters.ContainsKey("-companionaiipctest");
    internal static string Mode { get; private set; }
    private static string Human() => string.Join("|", Main.player[0].armor.Concat(Main.player[0].inventory)
        .Select(i => $"{ActorInventory.Instance(i)}:{i.type}:{i.stack}:{i.prefix}"));
    private static string human;
    private static Item mouse;
    private static int local, mouseX, mouseY;
    private static Microsoft.Xna.Framework.Input.KeyboardState keys;
    private static (bool Left, bool Right, bool LeftRelease, bool RightRelease) pointer;
    private static int permissionCalls;
    internal static bool MutateOnSync;
    private static (bool, bool, bool, bool) Pointer() =>
        (Main.mouseLeft, Main.mouseRight, Main.mouseLeftRelease, Main.mouseRightRelease);

    internal static void Permission(Player p)
    {
        if (!ReferenceEquals(p, ProbeSession.Actor) || !ReferenceEquals(Main.CurrentPlayer, p)
            || Main.myPlayer != local || !ReferenceEquals(Main.mouseItem, mouse)
            || Main.mouseX != mouseX || Main.mouseY != mouseY || Main.keyState != keys || Pointer() != pointer
            || Human() != human)
            throw new InvalidOperationException("Equipment hook did not run in the isolated Actor context.");
        permissionCalls++;
    }

    internal static void Tick(Player p)
    {
        if (!Enabled) return;
        string request = Path.Combine(Main.SavePath, "equipment-request");
        if (!File.Exists(request)) return;
        string mode = File.ReadAllText(request).Trim();
        File.Delete(request);
        var mod = ModContent.GetInstance<EquipmentDiagnosticSlot>();
        if (mode == "reset")
        {
            for (int i = 0; i < 50; i++) p.inventory[i] = new Item();
            for (int i = 0; i < 10; i++) p.armor[i] = new Item();
            using var scope = new Main.CurrentPlayerOverride(p);
            mod.FunctionalItem = new Item();
            p.selectedItem = 0;
            var items = new[] { ItemID.CopperHelmet, ItemID.IronHelmet, ItemID.CopperChainmail, ItemID.IronChainmail,
                ItemID.CopperGreaves, ItemID.IronGreaves, ItemID.Aglet, ItemID.HermesBoots, ItemID.CloudinaBottle,
                ItemID.Shackle, ItemID.CloudinaBottle, ItemID.HermesBoots, ItemID.Wood, ItemID.BandofRegeneration };
            for (int i = 0; i < items.Length; i++) p.inventory[i + 10] = new Item(items[i]);
            p.inventory[11].favorited = true;
            p.inventory[24] = new Item(ModContent.ItemType<EquipmentDiagnosticAccessory>());
            human = Human(); mouse = Main.mouseItem; local = Main.myPlayer;
            mouseX = Main.mouseX; mouseY = Main.mouseY; keys = Main.keyState; pointer = Pointer();
            permissionCalls = 0;
            MutateOnSync = false;
        }
        else if (mode == "full")
        {
            for (int i = 0; i < 50; i++)
                if (p.inventory[i].IsAir) p.inventory[i] = new Item(ItemID.Wood);
        }
        else if (mode == "new-source") p.inventory[10] = new Item(ItemID.CopperHelmet);
        else if (mode == "new-target") p.armor[0] = new Item(ItemID.CopperHelmet);
        else if (mode == "lifecycle") Lifecycle(p);
        Mode = mode;
        if (Human() != human || !ReferenceEquals(Main.mouseItem, mouse) || local != Main.myPlayer
            || mouseX != Main.mouseX || mouseY != Main.mouseY || Main.keyState != keys || Pointer() != pointer)
            throw new InvalidOperationException("Equipment fixture observed shared player or UI state changes.");
        File.WriteAllText(Path.Combine(Main.SavePath, "equipment-ready.json"),
            JsonSerializer.Serialize(new { mode, permissionCalls, slot = mod.FullName }));
    }

    private static void Lifecycle(Player p)
    {
        var identity = new AiReadOnlyProtocol.Identity(p, ProbeSession.WorldSession, ProbeSession.ActorGeneration, p.whoAmI, p.selectedItem);
        string Target() => ActorInventory.Token(identity, p.armor[0], $"equipment:{p.CurrentLoadoutIndex}:vanilla:head");
        var rows = new System.Collections.Generic.List<string>();
        foreach (string scenario in new[] { "cancel_before", "cancel_after", "cancel_unequip_after", "death", "generation", "world" })
        {
            p.inventory[10] = new Item(ItemID.CopperHelmet); p.armor[0] = new Item();
            var helmet = p.inventory[10];
            var owner = Guid.NewGuid();
            var lifecycle = new AiAtomicActions(new ServerAiNativeActions());
            var binding = new AiReadOnlyProtocol.Binding("equipment-fixture", identity.World, identity.Generation, identity.Slot);
            var args = new { sourceSlot = 10, sourceToken = ActorInventory.Token(identity, helmet, 10),
                targetSlotId = "vanilla:head", targetToken = Target() };
            var root = JsonSerializer.SerializeToElement(new { actionId = Guid.NewGuid().ToString("N"), operation = "equip_item",
                arguments = args, expiresAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1500 });
            var captured = identity;
            void Pump() => lifecycle.Tick(() => captured, (_, _) => throw new InvalidOperationException("Unexpected selection."));
            if (lifecycle.Handle(owner, "start", root, identity, binding, default).Error != null)
                throw new InvalidOperationException("Equipment lifecycle start rejected.");
            Pump(); Pump();
            if (scenario != "cancel_before") Pump(); // Apply, but still in the same update.
            switch (scenario)
            {
                case "cancel_before": case "cancel_after": lifecycle.Handle(owner, "cancel", root, identity, binding, default); break;
                case "cancel_unequip_after":
                    var context = new ActorEquipment.Context(identity, "unequip_item", JsonSerializer.SerializeToElement(
                        new { targetSlotId = "vanilla:head", targetToken = Target(), destinationSlot = 10 }));
                    context.Step();
                    context.Cancel();
                    if (context.Status != "unknown" || !ReferenceEquals(p.inventory[10], helmet))
                        throw new InvalidOperationException("Pending unequip was rolled back or misclassified.");
                    lifecycle.Handle(owner, "cancel", root, identity, binding, default);
                    break;
                case "death": captured = identity with { CanAct = false }; break;
                case "generation": captured = identity with { Generation = identity.Generation + 1 }; break;
                case "world": captured = identity with { World = "different-world" }; break;
            }
            Pump();
            var receipt = JsonSerializer.SerializeToElement(lifecycle.Handle(owner, "action", root, identity, binding, default).Data)
                .GetProperty("action");
            string expected = scenario == "cancel_before" ? "cancelled" : "unknown";
            if (receipt.GetProperty("status").GetString() != expected
                || !ReferenceEquals(scenario is "cancel_before" or "cancel_unequip_after" ? p.inventory[10] : p.armor[0], helmet))
                throw new InvalidOperationException(scenario + ": " + receipt);
            rows.Add(scenario);
        }
        File.WriteAllText(Path.Combine(Main.SavePath, "equipment-lifecycle.json"), JsonSerializer.Serialize(rows));
    }
}

public sealed class EquipmentDiagnosticSlot : ModAccessorySlot
{
    public override bool IsLoadingEnabled(Mod mod) => ServerEquipmentFixture.Enabled;
    public override bool DrawFunctionalSlot => ServerEquipmentFixture.Mode != "hidden_functional";
    public override bool IsEnabled() => ServerEquipmentFixture.Mode != "disabled";
    public override bool CanAcceptItem(Item item, AccessorySlotType context)
    {
        ServerEquipmentFixture.Permission(Player);
        switch (ServerEquipmentFixture.Mode)
        {
            case "throw": throw new InvalidOperationException("Isolated accessory permission exception.");
            case "mutate": Player.inventory[35] = new Item(ItemID.Diamond); return true;
            case "sync-mutate": ServerEquipmentFixture.MutateOnSync = true; return true;
            case "reject": return false;
            default: return base.CanAcceptItem(item, context);
        }
    }
}

public sealed class EquipmentDiagnosticAccessory : ModItem
{
    public override bool IsLoadingEnabled(Mod mod) => ServerEquipmentFixture.Enabled;
    public override string Texture => "Terraria/Images/Item_" + ItemID.Aglet;
    public override void SetDefaults() { Item.accessory = true; Item.maxStack = 1; }
    public override bool CanEquipAccessory(Player player, int slot, bool modded)
    {
        ServerEquipmentFixture.Permission(player);
        return modded;
    }
}

public sealed class EquipmentDiagnosticSync : ModPlayer
{
    public override bool IsLoadingEnabled(Mod mod) => ServerEquipmentFixture.Enabled;
    public override void SyncPlayer(int toWho, int fromWho, bool newPlayer)
    {
        if (!ServerEquipmentFixture.MutateOnSync || !ReferenceEquals(Player, ProbeSession.Actor)) return;
        ServerEquipmentFixture.MutateOnSync = false;
        Player.inventory[36] = new Item(ItemID.Diamond);
    }
}
