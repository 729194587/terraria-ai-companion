using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Copied-world diagnostic. The donor logs in and updates its own inventory over TCP.
public sealed class ServerPotionBuffFixture : ModSystem
{
    public override bool IsLoadingEnabled(Mod mod) => Main.dedServ && ServerAiActionFixture.Enabled
        && Program.LaunchParameters.ContainsKey("-companionpotiontest");
    public override void Load() => Main.OnTickForThirdPartySoftwareOnly += Tick;
    public override void Unload() => Main.OnTickForThirdPartySoftwareOnly -= Tick;

    private static void Tick()
    {
        var request = Path.Combine(Main.SavePath, "potion-request.json");
        if (!Program.IsMainThread || !File.Exists(request)) return;
        object result;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(request));
            var cmd = doc.RootElement;
            var actor = ProbeSession.Actor;
            NetworkStateSync.Require(actor);
            int stack = cmd.TryGetProperty("stack", out var number) ? number.GetInt32() : 0;
            var sample = stack > 0 ? new Item(ItemID.IronskinPotion, stack) : new Item();
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            switch (cmd.GetProperty("op").GetString())
            {
                case "stock":
                    actor.inventory[0] = sample;
                    actor.selectedItem = 0;
                    break;
                case "potion-bag":
                    if (!ModLoader.TryGetMod("ImproveGame", out var quality)) throw new InvalidOperationException();
                    var bag = new Item(quality.Find<ModItem>("PotionBag").Type);
                    var contents = (List<Item>)bag.ModItem.GetType().GetProperty("ItemContainer").GetValue(bag.ModItem);
                    contents.Add(sample);
                    actor.inventory[0] = bag;
                    break;
                case "near-expiry":
                    actor.buffTime[actor.FindBuffIndex(BuffID.Ironskin)] = 6;
                    actor.AddBuff(BuffID.Sharpened, 90, quiet: true);
                    break;
                case "wire-item":
                    ItemIO.Send(sample, writer, true, false);
                    break;
                case "state":
                    break;
                default: throw new InvalidDataException("Unknown potion fixture command");
            }
            var donor = Main.player.FirstOrDefault(p => p?.active == true && p.name == "PotionDonor");
            int Time(int type)
            {
                int i = actor.FindBuffIndex(type);
                return i < 0 ? 0 : actor.buffTime[i];
            }
            result = new { tick = Main.GameUpdateCount, actor = actor.whoAmI, actor.team,
                x = actor.position.X, y = actor.position.Y, defense = (int)actor.statDefense,
                ironSlot = actor.FindBuffIndex(BuffID.Ironskin), iron = Time(BuffID.Ironskin),
                sharpened = Time(BuffID.Sharpened), stack = actor.inventory[0].stack,
                donor = donor == null ? null : new { slot = donor.whoAmI, donor.team,
                    stack = donor.inventory[1].stack, distance = actor.Distance(donor.Center),
                    state = Netplay.Clients[donor.whoAmI].State },
                itemPayload = Convert.ToBase64String(stream.ToArray()) };
        }
        catch (Exception e) { result = new { error = e.ToString() }; }
        File.Delete(request);
        var response = Path.Combine(Main.SavePath, "potion-response.json");
        File.WriteAllText(response + ".tmp", JsonSerializer.Serialize(result));
        File.Move(response + ".tmp", response, true);
    }
}
