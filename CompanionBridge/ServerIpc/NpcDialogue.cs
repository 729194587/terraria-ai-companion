using System;
using System.Text.Json;
using Terraria;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal static class NpcDialogue
{
    internal sealed class Conversation(Identity id, NPC npc, string reference, string text)
        : AiReadOnlyProtocol.IDialogue
    {
        private Player Actor => (Player)id.Actor;
        internal string Reference => reference;
        internal string Text => text;
        public bool IsCurrent => ServerAiNativeActions.Owned(id) && Actor.talkNPC == npc.whoAmI
            && ReferenceEquals(Main.npc[npc.whoAmI], npc) && npc.active && npc.life > 0
            && NativeCombatWorld.Reference(id, npc) == reference
            && !Actor.dead && !Actor.ghost && Actor.Distance(npc.Center) <= 80f && npc.CanTalk;

        public void Dispose()
        {
            if (ServerAiNativeActions.Owned(id) && Actor.talkNPC == npc.whoAmI)
                Actor.SetTalkNPC(-1);
        }
    }

    internal static NPC Resolve(Identity id, string reference)
    {
        foreach (var npc in Main.ActiveNPCs)
            if (NativeCombatWorld.Reference(id, npc) == reference) return npc;
        return null;
    }

    internal static string UnavailableReason(Player player, NPC npc) =>
        player.dead || player.ghost ? "actor_cannot_talk" :
        player.Distance(npc.Center) > 80f ? "npc_out_of_range" :
        !npc.CanTalk ? "npc_cannot_talk" : null;

    internal static Result Dialogue(Identity id, JsonElement args, bool open)
    {
        var lease = id.RequestLease;
        var player = (Player)id.Actor;
        Result Failed(string reason) => new("failed", reason, new { open = player.talkNPC >= 0 }, true, InputsAcquired:false);
        if (lease == null || !MerchantShop.VanillaServicesSupported) return Failed("dialogue_unavailable");
        if (!open)
        {
            lease.EndDialogue();
            if (player.talkNPC >= 0) return Failed("dialogue_owned_elsewhere");
            return new("completed", "dialogue_closed", new { open = false }, true, true, InputsAcquired:false);
        }

        string entityRef = args.GetProperty("entityRef").GetString();
        var npc = Resolve(id, entityRef);
        string error = npc == null || npc.life <= 0 ? "stale_entity_ref" : UnavailableReason(player, npc);
        // Resolution only reads the target. No existing session has been closed yet.
        if (error != null) return new("failed", error, new { operation = "open_npc_dialogue",
            open = player.talkNPC >= 0, mutationAttempted = false, effectStatus = "not_started" },
            true, InputsAcquired:false);
        Result Opened(string text) => new("completed", "dialogue_opened", new { open = true, entityRef, text },
            true, true, InputsAcquired:false);
        if (lease.Dialogue is Conversation existing && existing.IsCurrent && existing.Reference == entityRef)
            return Opened(existing.Text);
        lease.EndDialogue();
        if (player.talkNPC >= 0) return Failed("dialogue_owned_elsewhere");

        // Vanilla GetChat reads Main.player[Main.myPlayer] and resets two shared dialogue flags.
        int previousSlot = Main.myPlayer;
        bool previousCola = NPC.PreventJojaColaDialog;
        int previousDryad = NPC.RerollDryadText;
        try
        {
            Main.myPlayer = id.Slot;
            string text = npc.GetChat();
            if (string.IsNullOrWhiteSpace(text)) return Failed("dialogue_text_unavailable");
            var session = new Conversation(id, npc, entityRef, text);
            lease.Dialogue = session;
            player.SetTalkNPC(npc.whoAmI);
            if (!session.IsCurrent)
            {
                lease.EndDialogue();
                return Failed("dialogue_open_unconfirmed");
            }
            return Opened(text);
        }
        catch
        {
            lease.EndDialogue();
            throw;
        }
        finally
        {
            Main.myPlayer = previousSlot;
            NPC.PreventJojaColaDialog = previousCola;
            NPC.RerollDryadText = previousDryad;
        }
    }
}
