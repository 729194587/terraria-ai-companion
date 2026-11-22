// Drop identity lifecycle for the ACTUAL ServerDrops.Reference/Live/ResetIdentity.
// The engine surface is a deterministic substitute; identity rules are production code.
using System;
using System.Linq;
using CompanionBridge.ServerIpc;
using Terraria;
using Terraria.ModLoader;

public static class DropIdentityOfflineTests
{
    private static int checks;
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception(text);
        checks++;
        Console.WriteLine("PASS drop identity: " + text);
    }

    private static readonly AiReadOnlyProtocol.Identity Id = new(new object(), "world", 1, 254);

    private static ServerDrops.Drop Observe(int slot)
    {
        var item = Main.item[slot];
        return new(slot, item, ServerDrops.Reference(Id, slot, item), item.type, item.prefix);
    }

    public static void Run()
    {
        // The production hook class is what reinitialisation calls. Wire its two
        // entry points into the substitute so the reset wiring is exercised.
        ItemLoader.DefaultsHook = item => new DropIdentityGlobalItem().SetDefaults(item);
        ItemLoader.SpawnHook = (item, source) => new DropIdentityGlobalItem().OnSpawn(item, source);

        Main.item = Enumerable.Range(0, 8).Select(_ => new Item { active = false, type = 0, stack = 0 }).ToArray();

        // 1. Baseline: one live object keeps one stable reference.
        Main.item[3] = new Item { type = 9, stack = 7 };
        var first = Observe(3);
        Check(ServerDrops.Live(first), "a live observed drop keeps its reference");
        Check(ServerDrops.Reference(Id, first.Slot, first.Item) == first.Reference, "re-observing the same object yields the same reference");
        Check(first.Reference.StartsWith("world:1:254:", StringComparison.Ordinal), "reference is scoped to world, generation and actor slot");

        // 2. Slot reuse: a different Item object in the same slot must not satisfy
        //    the old reference, even with the identical type and prefix.
        Main.item[3] = new Item { type = 9, stack = 7 };
        Check(!ServerDrops.Live(first), "a replaced object in the same slot invalidates the old reference");
        var replacement = Observe(3);
        Check(replacement.Reference != first.Reference, "the replacement object gets a distinct reference");
        Check(ServerDrops.Live(replacement), "the replacement's own reference is live");

        // 3. Reinitialisation of the SAME object through SetDefaults must drop the
        //    old identity, because the object now represents a new drop.
        Main.item[4] = new Item { type = 9, stack = 5, prefix = 2 };
        var beforeReset = Observe(4);
        var sameObject = Main.item[4];
        sameObject.SetDefaults(9);
        sameObject.stack = 5;
        sameObject.prefix = 2;
        Check(ReferenceEquals(Main.item[4], sameObject), "reinitialisation keeps the same Item instance");
        Check(!ServerDrops.Live(beforeReset), "reinitialising the same object invalidates the previous reference");
        var afterReset = Observe(4);
        Check(afterReset.Reference != beforeReset.Reference, "the reinitialised drop gets a fresh reference");
        Check(ServerDrops.Live(afterReset), "the reinitialised drop's new reference is live");

        // 4. OnSpawn is the other native reinitialisation signal and must reset too.
        Main.item[5] = new Item { type = 3, stack = 2 };
        var beforeSpawn = Observe(5);
        ItemLoader.OnSpawn(Main.item[5], null);
        Main.item[5].stack = 2;
        Check(!ServerDrops.Live(beforeSpawn), "OnSpawn reinitialisation invalidates the previous reference");

        // 5. Clone copies through MemberwiseClone and runs no hook. The copied object
        //    must not inherit the source stamp, or a stale reference could name it.
        Main.item[6] = new Item { type = 9, stack = 4 };
        var source = Observe(6);
        var clone = Main.item[6].Clone();
        Main.item[6] = clone;
        Check(!ReferenceEquals(clone, source.Item), "clone is a different object");
        Check(!ServerDrops.Live(source), "a cloned object does not inherit the source identity");
        var cloned = Observe(6);
        Check(cloned.Reference != source.Reference, "the clone receives its own reference");
        Check(ServerDrops.Live(cloned), "the clone's own reference is live");

        // 6. Metadata is part of the observation: a prefix change makes the old
        //    observation stale, while the same instance keeps its identity stamp.
        Main.item[7] = new Item { type = 9, stack = 6, prefix = 0 };
        var prefixed = Observe(7);
        Main.item[7].prefix = 1;
        Check(!ServerDrops.Live(prefixed), "a prefix change invalidates the observation");
        Main.item[7].prefix = 0;
        Check(ServerDrops.Live(prefixed), "the same instance with the observed metadata is live again");
        var restored = Observe(7);
        Check(restored.Reference == prefixed.Reference, "the same object retains its identity across metadata changes");
        Check(ServerDrops.Live(restored), "a freshly observed unchanged object is live again");

        // 7. Inactive, empty and moved-away objects are not live references.
        Main.item[7].active = false;
        Check(!ServerDrops.Live(restored), "an inactive object is not a live drop");
        Main.item[7].active = true;
        Main.item[7].stack = 0;
        Check(!ServerDrops.Live(restored), "an empty object is not a live drop");
        Main.item[7].stack = 6;
        // Liveness tracks the instance and its observed metadata: the same object
        // with the same observed type/prefix/stack is live again. Only identity
        // changes (new object, reinitialisation, clone) retire a reference for good.
        Check(ServerDrops.Live(restored), "the same live object with its observed metadata is live again");
        Main.item[7] = new Item { type = 9, stack = 6 };
        Check(!ServerDrops.Live(restored), "moving the object out of its slot invalidates the reference");

        // 8. The reference is scoped by world, generation and slot, so the same
        //    object observed under another binding is never the same reference.
        Main.item[0] = new Item { type = 9, stack = 1 };
        var local = Observe(0);
        var otherWorld = new AiReadOnlyProtocol.Identity(Id.Actor, "world-2", 1, 254);
        var otherGeneration = new AiReadOnlyProtocol.Identity(Id.Actor, "world", 2, 254);
        var otherSlot = new AiReadOnlyProtocol.Identity(Id.Actor, "world", 1, 7);
        Check(ServerDrops.Reference(otherWorld, local.Slot, local.Item) != local.Reference, "another world produces a different reference");
        Check(ServerDrops.Reference(otherGeneration, local.Slot, local.Item) != local.Reference, "another generation produces a different reference");
        Check(ServerDrops.Reference(otherSlot, local.Slot, local.Item) != local.Reference, "another actor slot produces a different reference");
        Main.item[0] = new Item();
        Main.item[2] = local.Item;
        Check(!ServerDrops.Live(local), "moving the same object to a different slot invalidates the old observation");
        Check(Observe(2).Reference != local.Reference, "a different drop slot cannot resolve the old reference");
        Main.item[0] = local.Item;
        Main.item[2] = new Item();

        // 9. A replacement that happens to reuse the slot must never inherit the
        //    old reference merely because the type, prefix and stack match.
        Main.item[1] = new Item { type = 3, stack = 5, prefix = 0 };
        var original = Observe(1);
        Main.item[1] = new Item { type = 3, stack = 5, prefix = 0 };
        Check(!ServerDrops.Live(original), "identical type/prefix/stack in a reused slot does not adopt the old reference");
        Check(Observe(1).Reference != original.Reference, "the identical replacement has its own reference");

        // 10. A slot outside the current array, or a null observation, is never live
        //     instead of throwing on a world unload or array replacement.
        var outOfRange = new ServerDrops.Drop(9999, new Item { type = 9, stack = 1 }, "world:1:254:x", 9, -1);
        Check(!ServerDrops.Live(outOfRange), "an out-of-range slot is not a live drop");
        Check(!ServerDrops.Live(null), "a null observation is not a live drop");
        var nullItem = new ServerDrops.Drop(0, null, "world:1:254:x", 9, -1);
        Check(!ServerDrops.Live(nullItem), "an observation without an item is not a live drop");
        var saved = Main.item;
        Main.item = null;
        try { Check(!ServerDrops.Live(local), "a missing item array cannot keep a drop live"); }
        finally { Main.item = saved; }
        Check(ServerDrops.Live(local), "restoring the item array restores liveness for the same object");

        Console.WriteLine($"Drop identity lifecycle: {checks} checks; engine substitute, production identity rules.");
    }
}
