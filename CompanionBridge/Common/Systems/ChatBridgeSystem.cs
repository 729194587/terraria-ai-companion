using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Chat;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace CompanionBridge.Common.Systems;

public sealed class ChatBridgeSystem : ModSystem
{
    public override void Load()
    {
        if (Main.dedServ) return;
        On_ChatHelper.DisplayMessage += Receive;
        On_ChatHelper.SendChatMessageFromClient += Submit;
        On_Main.NewText_string_byte_byte_byte += ReceiveLocal;
        On_Main.NewTextMultiline += ReceiveMultiline;
        On_ChatHelper.ShowCachedMessages += ReplayCached;
    }

    private static void Receive(On_ChatHelper.orig_DisplayMessage orig, NetworkText text, Color color, byte author)
    {
        GameMessageCapture.Run(() => orig(text, color, author), () => {
            if (!InWorld) return;
            if (author == byte.MaxValue) PublishSystem(text.ToString(), "chat_helper");
            else if (Main.netMode == NetmodeID.MultiplayerClient && author < Main.maxPlayers
                && author != Main.myPlayer && Main.player[author].active)
                CompanionBridge.Server?.PublishChat(author, Main.player[author].name, text.ToString(), Main.myPlayer);
        });
    }

    private static bool InWorld => !Main.dedServ && !Main.gameMenu && Main.LocalPlayer.active
        && Main.netMode is NetmodeID.SinglePlayer or NetmodeID.MultiplayerClient
        && !(Main.netMode == NetmodeID.MultiplayerClient && Netplay.Disconnect);

    private static void Submit(On_ChatHelper.orig_SendChatMessageFromClient orig, ChatMessage message)
    {
        // Own replies are suppressed, even if their text begins with !ai.
        GameMessageCapture.Run(() => orig(message), () => {
            if(InWorld&&Main.netMode==NetmodeID.MultiplayerClient)
                CompanionBridge.Server?.PublishLocalAiCommand(Main.myPlayer,Main.LocalPlayer.name,message.Text);
        });
    }

    private static void PublishSystem(string text, string source)
    {
        if (InWorld) CompanionBridge.Server?.PublishGameMessage(text, source, Main.GameUpdateCount);
    }

    private static void ReceiveLocal(On_Main.orig_NewText_string_byte_byte_byte orig, string text, byte r, byte g, byte b) =>
        GameMessageCapture.Run(() => orig(text, r, g, b), () => PublishSystem(text, "main_new_text"));

    private static void ReceiveMultiline(On_Main.orig_NewTextMultiline orig, string text, bool force, Color color, int width) =>
        GameMessageCapture.Run(() => orig(text, force, color, width), () => PublishSystem(text, "main_new_text_multiline"));

    private static void ReplayCached(On_ChatHelper.orig_ShowCachedMessages orig) => GameMessageCapture.Suppress(() => orig());

    // Invoked only by ControlState.Update on the game thread. ChatMessage(string) uses SayChatCommand.
    internal static void Send(string text)
    {
        GameMessageCapture.Suppress(() => {
            if (Main.netMode == NetmodeID.MultiplayerClient)
                ChatHelper.SendChatMessageFromClient(new ChatMessage(text));
            else
                Main.NewText(text);
        });
    }

    public override void Unload()
    {
        if (Main.dedServ) return;
        On_ChatHelper.DisplayMessage -= Receive;
        On_ChatHelper.SendChatMessageFromClient -= Submit;
        On_Main.NewText_string_byte_byte_byte -= ReceiveLocal;
        On_Main.NewTextMultiline -= ReceiveMultiline;
        On_ChatHelper.ShowCachedMessages -= ReplayCached;
    }
}
