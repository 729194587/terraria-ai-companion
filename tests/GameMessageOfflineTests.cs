using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CompanionBridge;

public static class GameMessageOfflineTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    public static void Run()
    {
        int displayed = 0, fallback = 0, captured = 0;
        GameMessageCapture.Run(() => GameMessageCapture.Run(() => displayed++, () => fallback++), () => captured++);
        Check(displayed == 1 && fallback == 0 && captured == 1, "ChatHelper and nested NewText produce one capture");
        GameMessageCapture.Suppress(() => GameMessageCapture.Run(() => displayed++, () => captured++));
        Check(displayed == 2 && captured == 1, "Own replies and cached display remain visible but unrecorded");
        GameMessageCapture.Run(() => displayed++, () => captured++);
        Check(captured == 2, "Direct local NewText captured");
        try { GameMessageCapture.Suppress(() => throw new InvalidOperationException("fixture")); }
        catch (InvalidOperationException) { }
        GameMessageCapture.Run(() => displayed++, () => captured++);
        Check(captured == 3, "Capture suppression restored after exception");

        var state = new ControlState();
        using var server = new TcpControlServer(state, 0);
        server.PublishGameMessage("before connection", "main_new_text", 1);
        server.Start();
        using var client = new TcpClient("127.0.0.1", server.Port);
        client.ReceiveTimeout = 2000;
        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 1024, true);
        Check(reader.ReadLine().Contains("game_messages_v1"), "Advertise passive game-message capability");
        int session = state.SessionId;
        server.PublishChat(0, "human", "What happened?", 1);
        using var chat = JsonDocument.Parse(reader.ReadLine());
        string chatId = chat.RootElement.GetProperty("eventId").GetString();

        server.PublishGameMessage(" ", "main_new_text", 2);
        server.PublishGameMessage("第50只史莱姆被击败", "chat_helper", 3);
        using var kill = JsonDocument.Parse(reader.ReadLine());
        Check(kill.RootElement.GetProperty("type").GetString() == "game_message"
            && kill.RootElement.GetProperty("source").GetString() == "chat_helper"
            && kill.RootElement.GetProperty("gameUpdate").GetUInt64() == 3
            && kill.RootElement.GetProperty("text").GetString() == "第50只史莱姆被击败", "System notice crosses TCP with source/time");
        server.PublishGameMessage("test drowned", "main_new_text", 4);
        using var death = JsonDocument.Parse(reader.ReadLine());
        Check(death.RootElement.GetProperty("eventId").GetString() != kill.RootElement.GetProperty("eventId").GetString(),
            "System event IDs are distinct");
        // A passive system message cannot supersede the player's pending answer, even while dead.
        var sent = state.Enqueue(session, new(1, "send_chat", Text: "fixture answer", ChatId: chatId), 0);
        int sends = 0;
        state.Update(false, 0, new(true, new(0, 0), new(0, 0), 0, 100), sendChat: _ => sends++);
        Check(sent.Result.Status == "sent" && sends == 1 && state.SessionId == session && !state.IsMoving,
            "Death/system notices preserve current chatId, read-only session and no control lease");

        server.PublishGameMessage(new string('x', 2000), "main_new_text_multiline", 5);
        using var longText = JsonDocument.Parse(reader.ReadLine());
        Check(longText.RootElement.GetProperty("text").GetString().Length == 1024
            && longText.RootElement.GetProperty("textTruncated").GetBoolean()
            && longText.RootElement.GetProperty("droppedBefore").GetInt32() == 0, "Bounded text declares truncation");
        server.PublishChat(0, "human", "again", 1);
        using var laterChat = JsonDocument.Parse(reader.ReadLine());
        Check(laterChat.RootElement.GetProperty("type").GetString() == "chat_message", "Player chat remains separate");
        Console.WriteLine("PASS: nested message capture, self/cache suppression, passive TCP notices, death/chat continuity and truncation");
    }
}
