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
        if (!Main.dedServ) On_ChatHelper.DisplayMessage += Receive;
    }

    private static void Receive(On_ChatHelper.orig_DisplayMessage orig, NetworkText text, Color color, byte author)
    {
        orig(text, color, author);
        if (!Main.gameMenu && Main.netMode == NetmodeID.MultiplayerClient && author < Main.maxPlayers
            && author != Main.myPlayer && Main.player[author].active)
            CompanionBridge.Server?.PublishChat(author, Main.player[author].name, text.ToString(), Main.myPlayer);
    }

    // Invoked only by ControlState.Update on the game thread. ChatMessage(string) uses SayChatCommand.
    internal static void Send(string text)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
            ChatHelper.SendChatMessageFromClient(new ChatMessage(text));
        else
            Main.NewText(text);
    }

    public override void Unload()
    {
        if (!Main.dedServ) On_ChatHelper.DisplayMessage -= Receive;
    }
}
