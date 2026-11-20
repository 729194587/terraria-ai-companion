using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Invoked only by the marked, isolated, no-client action fixture on the game thread.
internal static class G3ShopFixture
{
    private static AiReadOnlyProtocol.Identity Identity(Player p) =>
        new(p, ProbeSession.WorldSession, ProbeSession.ActorGeneration, p.whoAmI, null,
            p.selectedItem, Main.GameUpdateCount);

    private static object[] Slots(Item[] items) => items.Select((item, slot) =>
        (object)new { slot, item.type, item.stack, item.prefix, item.favorited }).ToArray();

    private static object Snapshot(Player p) => new {
        carried = Slots(p.inventory),
        banks = new[] { Slots(p.bank.item), Slots(p.bank2.item), Slots(p.bank3.item), Slots(p.bank4.item) }
    };

    private static bool NurseTest => Program.LaunchParameters.ContainsKey("-companionnursetest");
    private static bool ReforgeTest => Program.LaunchParameters.ContainsKey("-companionreforgetest");
    private static int VendorType => ReforgeTest ? NPCID.GoblinTinkerer : NurseTest ? NPCID.Nurse : NPCID.Merchant;

    private static int Spawn(Player p) => NPC.NewNPC(p.GetSource_Misc("G3ShopFixture"),
        (int)p.Center.X + 32, (int)p.Bottom.Y - 2, VendorType);

    private static bool InterruptPayment(On_Player.orig_PayCurrency orig, Player player, long price, int currency)
    {
        bool paid = orig(player, price, currency);
        if (paid && ReferenceEquals(player, ProbeSession.Actor))
        {
            On_Player.PayCurrency -= InterruptPayment;
            throw new InvalidOperationException("Isolated interruption after native payment");
        }
        return paid;
    }

    internal static void Tick(Player p)
    {
        string PathFor(string name) => Path.Combine(Main.SavePath, "g3-shop-" + name);
        if (!File.Exists(PathFor("ready.json")))
        {
            if (Program.LaunchParameters.ContainsKey("-companionmerchantluatest"))
            {
                // Keep the actors in the same small stall while the quote ages.
                // NPC movement, CanTalk, item use and transaction preflight stay native.
                int left = (int)p.position.X / 16 - 1;
                int floor = Main.spawnTileY + 8;
                for (int x=left; x<=left+5; x++)
                    for (int y=floor-4; y<floor; y++)
                        if (x==left || x==left+5 || y==floor-4)
                        {
                            var tile = Main.tile[x,y];
                            tile.HasTile = true;
                            tile.TileType = TileID.WoodBlock;
                        }
            }
            int slot = Spawn(p);
            if (slot >= Main.maxNPCs) throw new InvalidOperationException("No shop fixture NPC slot");
            // Non-currency sentinels must survive spending and change-making.
            foreach (var bank in new[] { p.bank, p.bank2, p.bank3, p.bank4 })
                bank.item[3].SetDefaults(ItemID.StoneBlock);
            File.WriteAllText(PathFor("ready.json"), JsonSerializer.Serialize(new { slot }));
            return;
        }
        if (Program.LaunchParameters.ContainsKey("-companionmerchantluatest"))
        {
            // Lua uses the production IPC path. This fixture only funds the actor,
            // replaces an NPC instance, or snapshots the resulting inventories.
            if (!File.Exists(PathFor("control.json"))) return;
            string command;
            try
            {
                command = File.ReadAllText(PathFor("control.json"));
                File.Delete(PathFor("control.json"));
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                return; // Windows can briefly hold the renamed command. No mutation yet.
            }
            using var control = JsonDocument.Parse(command);
            string op = control.RootElement.GetProperty("op").GetString();
            using var ready = JsonDocument.Parse(File.ReadAllText(PathFor("ready.json")));
            int slot = ready.RootElement.GetProperty("slot").GetInt32();
            if (op == "fund")
            {
                p.bank.item[0].SetDefaults(ItemID.PlatinumCoin);
            }
            else if (op == "balances")
            {
                var amounts = control.RootElement.GetProperty("copper").EnumerateArray().Select(e => e.GetInt32()).ToArray();
                var stores = new[] { p.inventory, p.bank.item, p.bank2.item, p.bank3.item, p.bank4.item };
                if (amounts.Length != stores.Length || amounts.Any(n => n < 0 || n > 9999))
                    throw new InvalidOperationException("Invalid fixture funding");
                for (int i = 0; i < stores.Length; i++)
                {
                    foreach (var coin in stores[i].Where(item => item.IsACoin)) coin.TurnToAir();
                    if (amounts[i] == 0) continue;
                    var slotItem = stores[i][i == 0 ? 50 : 0];
                    if (!slotItem.IsAir) throw new InvalidOperationException("Fixture slot occupied");
                    slotItem.SetDefaults(ItemID.CopperCoin);
                    slotItem.stack = amounts[i];
                }
            }
            else if (op == "fillInventory")
            {
                foreach (var item in p.inventory.Take(54).Where(item => item.IsAir || item.IsACoin))
                    item.SetDefaults(ItemID.StoneBlock);
            }
            else if (op == "interruptPayment")
            {
                On_Player.PayCurrency += InterruptPayment;
            }
            else if (NurseTest && op == "injure")
            {
                p.statLife -= 10;
                p.AddBuff(BuffID.Bleeding,36000);
                p.AddBuff(BuffID.PotionSickness,36000);
            }
            else if (op == "replace")
            {
                if (!Main.npc[slot].active || Main.npc[slot].type != VendorType)
                    throw new InvalidOperationException("Fixture merchant missing");
                Main.npc[slot].active = false;
                if (Spawn(p) != slot) throw new InvalidOperationException("Fixture did not reuse merchant slot");
            }
            else if (op != "snapshot") throw new InvalidOperationException("Unexpected fixture command");
            var answer = new { id = control.RootElement.GetProperty("id").GetString(), inventory = Snapshot(p), life=p.statLife, buffs=p.buffType.Where(t=>t!=0).ToArray(),
                entityRef = NativeCombatWorld.Reference(Identity(p), Main.npc[slot]) };
            File.WriteAllText(PathFor("control-result.tmp"), JsonSerializer.Serialize(answer));
            File.Move(PathFor("control-result.tmp"), PathFor("control-result.json"), true);
            return;
        }
        if (!File.Exists(PathFor("request.json")) || File.Exists(PathFor("started"))) return;
        File.WriteAllText(PathFor("started"), "Do not replay a transaction after an exception.");
        try
        {
            var id = Identity(p);
            using var request = JsonDocument.Parse(File.ReadAllText(PathFor("request.json")));
            int slot = request.RootElement.GetProperty("slot").GetInt32();
            var merchant = Main.npc[slot];
            string observed = request.RootElement.GetProperty("entityRef").GetString();
            string current = NativeCombatWorld.Reference(id, merchant);
            if (current != observed || !merchant.active || merchant.type != VendorType)
                throw new InvalidOperationException("Observed merchant changed before test");
            var catalog = G3ShopProbe.Catalog(p, merchant);
            object Try(string reference, bool purchase)
            {
                object before = Snapshot(p);
                object result = G3ShopProbe.Run(p, merchant, current, reference, purchase);
                return new { before, result, after = Snapshot(p) };
            }
            var quote = Try(observed, false);
            var insufficient = Try(observed, true);
            // Test endowment only, outside the measured purchase and refusal intervals.
            if (!p.inventory[50].IsAir) throw new InvalidOperationException("Fixture coin slot is occupied");
            p.bank.item[0].SetDefaults(ItemID.SilverCoin);
            p.bank.item[0].stack = 2;
            var purchase = Try(observed, true);
            merchant.active = false;
            if (Spawn(p) != slot) throw new InvalidOperationException("Fixture did not reuse merchant slot");
            merchant = Main.npc[slot];
            current = NativeCombatWorld.Reference(id, merchant);
            if (current == observed) throw new InvalidOperationException("Merchant identity was not refreshed");
            var stale = Try(observed, true);
            var evidence = new { id.World, id.Generation, id.Slot, merchantSlot = slot,
                observed, replacement = current, catalog, quote, insufficient, purchase, stale };
            File.WriteAllText(PathFor("result.tmp"), JsonSerializer.Serialize(evidence));
            File.Move(PathFor("result.tmp"), PathFor("result.json"));
        }
        catch (Exception e)
        {
            File.WriteAllText(PathFor("error.txt"), e.ToString());
            throw;
        }
    }
}
