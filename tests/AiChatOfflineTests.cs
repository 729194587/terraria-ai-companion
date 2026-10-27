using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CompanionBridge;

public static class AiChatOfflineTests
{
    private static int checks;
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
    public static void Run()
    {
        foreach(string text in new[]{null,"","  ","!ai","!ai ","/help","  /help","x\ny","x\0y",new string('x',2001)})
            Check(!AiChatInput.TryParse(text,out _),"empty/commands/invalid chat must not become an AI task");
        foreach(string text in new[]{"hello","你好","!air move","prefix !ai move","!AI move"})
            Check(AiChatInput.TryParse(text,out var plain)&&plain==text,"ordinary local speech is preserved without a required prefix");
        Check(AiChatInput.TryParse("  你好  ",out var trimmed)&&trimmed=="你好","trim only surrounding whitespace");
        Check(AiChatInput.TryParse("!ai 向右移动80像素",out var task)&&task=="向右移动80像素","legacy exact prefix remains optional and strips only the entry prefix");
        Check(AiChatInput.TryParse(new string('x',2000),out _)&&AiChatInput.TryParse("!ai "+new string('x',2000),out _),"plain and prefixed tasks retain the 2000-character limit");
        int sends=0,captures=0;
        GameMessageCapture.Suppress(()=>GameMessageCapture.Run(()=>sends++,()=>captures++));
        Check(sends==1&&captures==0,"AI replies remain visible but cannot enter outgoing command capture");
        var state=new ControlState();using var server=new TcpControlServer(state,0);server.Start();
        using var client=new TcpClient("127.0.0.1",server.Port);client.ReceiveTimeout=2000;
        using var reader=new StreamReader(client.GetStream(),Encoding.UTF8,false,1024,true);
        Check(reader.ReadLine().Contains("local_ai_chat_v2"),"ordinary local AI chat capability advertised");
        server.PublishLocalAiCommand(0,"local","向右移动80像素");
        using var first=JsonDocument.Parse(reader.ReadLine());var e=first.RootElement;
        string id=e.GetProperty("eventId").GetString();
        Check(e.GetProperty("source").GetString()=="local_ai_command"&&e.GetProperty("isSelf").GetBoolean()
            &&e.GetProperty("text").GetString()==task,"local command crosses real TCP with explicit source and self identity");
        int session=state.SessionId;
        // Ordinary remote chat still feeds the legacy Agent; it must not revoke AI reply ownership.
        server.PublishChat(1,"other","ordinary chat",0);using var remote=JsonDocument.Parse(reader.ReadLine());
        var reply=state.Enqueue(session,new(1,"send_ai_chat",Text:"[AI] 完成",ChatId:id),0);
        state.Update(true,0,new(true,new(100,200),new(0,0),100,100),sendChat:_=>sends++);
        Check(reply.Result.Status=="sent"&&sends==2&&!state.IsMoving,"ordinary chat does not supersede AI reply or acquire human input");
        server.PublishLocalAiCommand(0,"local","停止");using var second=JsonDocument.Parse(reader.ReadLine());
        string newer=second.RootElement.GetProperty("eventId").GetString();
        var old=state.Enqueue(session,new(2,"send_ai_chat",Text:"stale",ChatId:id),0);
        var fresh=state.Enqueue(session,new(3,"send_ai_chat",Text:"new",ChatId:newer),0);
        state.Update(true,0,new(true,new(100,200),new(0,0),100,100),sendChat:_=>sends++);
        Check(old.Result.Status=="superseded"&&fresh.Result.Status=="sent"&&sends==3,"new unprefixed local message rejects old replies");
        state.Update(false,0,PlayerSnapshot.NotInWorld);
        var afterWorld=state.Enqueue(session,new(4,"send_ai_chat",Text:"stale world",ChatId:newer),0);
        state.Update(true,0,new(true,new(100,200),new(0,0),100,100),sendChat:_=>sends++);
        Check(afterWorld.Result.Status=="superseded"&&sends==3,"leaving world invalidates local AI reply identity");
        var legacy=state.Enqueue(session,new(5,"send_chat",Text:"legacy",ChatId:remote.RootElement.GetProperty("eventId").GetString()),0);
        state.Update(true,0,new(true,new(100,200),new(0,0),100,100),sendChat:_=>sends++);
        Check(legacy.Result.Status=="sent","legacy other-player chat path remains independent");
        var auth=state.Enqueue(session,new(6,"authorize_ai_reply",Text:"AI text",ChatId:newer,ReplyTurn:42),0);
        state.ObserveAiChat(newer);
        state.Update(true,0,new(true,new(100,200),new(0,0),100,100),sendChat:_=>throw new Exception("authorization must not speak as the human"));
        Check(auth.Result.Status=="authorized"&&!state.IsMoving,"reply authorization does not send chat or acquire human input");
        var authorization=JsonSerializer.SerializeToElement(auth.Result.Data);
        Check(authorization.GetProperty("chatId").GetString()==newer&&authorization.GetProperty("replyTurn").GetInt64()==42,"authorization echoes immutable chat and reply turn");
        state.ObserveAiChat("next");
        var staleAuth=state.Enqueue(session,new(7,"authorize_ai_reply",Text:"stale",ChatId:newer),0);
        state.Update(true,0,new(true,new(100,200),new(0,0),100,100));
        Check(staleAuth.Result.Status=="superseded","new command refuses old reply authorization");
        var parsed=ControlState.Parse("{\"v\":1,\"id\":7,\"cmd\":\"send_ai_chat\",\"text\":\"reply\",\"chatId\":\"id\"}");
        Check(parsed.Cmd=="send_ai_chat"&&parsed.Text=="reply","AI reply uses existing bounded text parser");
        foreach(string turn in new[]{"",",\"replyTurn\":0",",\"replyTurn\":-1",",\"replyTurn\":1.5"})
        {
            bool rejected=false;
            try{ControlState.Parse("{\"v\":1,\"id\":8,\"cmd\":\"authorize_ai_reply\",\"text\":\"reply\",\"chatId\":\"id\""+turn+"}");}
            catch(FormatException){rejected=true;}
            Check(rejected,"authorization rejects missing/invalid reply turn");
        }
        server.PublishLocalAiCommand(0,"local","/help");
        server.PublishLocalAiCommand(0,"local","!ai 查询状态");
        using var compatible=JsonDocument.Parse(reader.ReadLine());
        Check(compatible.RootElement.GetProperty("text").GetString()=="查询状态","slash commands do not emit tasks; legacy prefixed tasks remain compatible over TCP");
        Console.WriteLine($"AI chat offline: {checks} checks; real loopback, no graphical game.");
    }
}
