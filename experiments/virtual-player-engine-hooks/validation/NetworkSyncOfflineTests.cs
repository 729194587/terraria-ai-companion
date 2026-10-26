using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using VirtualPlayerEngineProbe;

// Execute complete installed native sync bodies and packaged G2 adapter bodies.
// Only storage, transport and unrelated announcement/world effects are fixtures.
public static class NetworkSyncOfflineTests
{
    public class ItemState { public int prefix, type, stack; public string customData; public void SetDefaults(int id) => type=id; }
    public class LoadoutState { public ItemState[] Armor = Items(20), Dye = Items(10); }
    private static ItemState[] Items(int count) => Enumerable.Range(0, count).Select(_ => new ItemState()).ToArray();
    public class PlayerState : EngineHookOfflineTests.PlayerState
    {
        public int chest, CurrentLoadoutIndex;
        public int hair, skinVariant, selectedItem;
        public ColorState shirtColor, hairColor, underShirtColor, pantsColor, shoeColor, eyeColor, skinColor;
        public ItemState[] inventory = Items(59), armor = Items(20), dye = Items(10), miscEquips = Items(5), miscDyes = Items(5);
        public LoadoutState[] Loadouts = Enumerable.Range(0, 3).Select(_ => new LoadoutState()).ToArray();
    }
    public class RemoteState : EngineHookOfflineTests.RemoteState
    {
        public bool IsAnnouncementCompleted;
        public string Name = "human";
    }
    public static class MainState
    {
        public static PlayerState[] player;
        public static bool dedServ = true;
        public static int netMode = 2;
        public static ulong GameUpdateCount;
        public static ulong get_GameUpdateCount() => GameUpdateCount;
    }
    public static class NetState { public static RemoteState[] Clients; }
    public struct ColorState { public int R,G,B; public ColorState(int r, int g, int b) { R=r;G=g;B=b; } }
    public class LocalizedState
    {
        public string Key = "fixture";
        public string Format(object[] args) => "fixture";
    }
    public static class LangState { public static LocalizedState[] mp = Enumerable.Range(0, 30).Select(i => new LocalizedState { Key="native-mp-"+i }).ToArray(); }
    public static class ItemSlots
    {
        public static int Inventory0 = 0, Armor0 = 59, Dye0 = 79, Misc0 = 89, MiscDye0 = 94;
        public static int Loadout1_Armor_0 = 99, Loadout1_Dye_0 = 119, Loadout2_Armor_0 = 129,
            Loadout2_Dye_0 = 149, Loadout3_Armor_0 = 159, Loadout3_Dye_0 = 179;
    }
    public class RevengeState { public void SendAllMarkersToPlayer(int id) { } }
    public static class NpcState { public static RevengeState RevengeManager = new(); }
    public readonly record struct Packet(int Type, int Remote, int Ignore, int Slot, float Value, bool Active);
    public readonly record struct Notice(string Key, string Name, int R, int G, int B, int Ignore);
    public static class Effects
    {
        public static bool ManagedDead(PlayerState p)=>false;
        public static void DeadSnapshot(PlayerState p,int toWho,int fromWho) { }
        public static StartupReservation Reservation;
        public static PlayerState Actor;
        public static bool Ready = true, Quarantined, EngineMainThread = true;
        public static string Fault, Phase;
        public static long FullSnapshots, Controls, Departures, SyncSkips;
        public static long NetworkLoops;
        public static ulong controlTick, fullTick;
        public static bool possiblyVisible, leaveAttempted;
        public static PlayerState owner;
        public static ItemState savedHelmet, debugHelmet;
        public static ColorState savedShirt;
        public static PlayerState joinedActor;
        public static string joinedName;
        public static bool joinNoticeAttempted, leaveNoticeAttempted, ThrowNotice;
        public static long JoinNotices, LeaveNotices, NoticeFailures;
        public static MethodInfo nativeFull = typeof(Effects).GetMethod(nameof(InvokeFull));
        public static Action<int, int, int> InvokePatched;
        public static List<Packet> Packets = new();
        public static List<Notice> Notices = new();
        public static int Announcements, Disconnects, ModPayloads;
        public static int ThrowPacket = -1;
        public static Action OnPacket;
        public static StartupReservation get_Reservation() => Reservation;
        public static string get_Fault() => Fault;
        public static bool get_IsMainThread() => EngineMainThread;
        public static bool IsReserved(int slot) => Reservation.IsReserved(slot);
        public static bool ConnectionIdle(RemoteState c) => c != null && StartupReservation.ConnectionIdle(
            c.IsConnected(), c.IsActive, c.State, c.PendingTermination, c.PendingTerminationApproved);
        public static void SignalFault(string text) => Fault ??= text;
        public static void InvokeFull(int slot, int remote, int ignore) => InvokePatched(slot, remote, ignore);
        public static void SendData(int type, int remote, int ignore, EngineHookOfflineTests.TextState text, int slot,
            float number2, float number3, float number4, int number5, int number6, int number7)
        {
            if (type == ThrowPacket) throw new IOException("fixture transport failure");
            Packets.Add(new(type, remote, ignore, slot, number2, MainState.player[slot].active));
            OnPacket?.Invoke();
        }
        public static void SyncPlayer(PlayerState player, int remote, int ignore, bool newPlayer) => ModPayloads++;
        public static void PlayerDisconnect(int slot) => Disconnects++;
        public static void BroadcastChatMessage(EngineHookOfflineTests.TextState text, ColorState color, int ignore)
        {
            Announcements++;
            Notices.Add(new(text.Key,text.Args?.FirstOrDefault() as string,color.R,color.G,color.B,ignore));
            if(ThrowNotice) throw new IOException("fixture chat failure after partial send");
        }
        public static void WarnAnnouncement(Exception _) { }
        public static void ServerConsoleLine(string line) { }
        public static void SendNPCHousesAndTravelShop(int id) { }
        public static void SendAnglerQuest(int id) { }
        public static void SendCreditsRollRemainingTimeToPlayer(int id) { }
        public static void EnsureLocalPlayerIsPresent() { }
    }

    private static int checks;
    private static void Check(bool result, string text)
    {
        if (!result) throw new Exception("FAIL G2: " + text);
        checks++; Console.WriteLine("PASS G2: " + text);
    }

    public static int Run(string gamePath, AssemblyDefinition probe)
    {
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(gamePath, "tModLoader.dll"));
        var native = game.MainModule.Types.Single(t => t.FullName == "Terraria.NetMessage");
        var one = native.Methods.Single(m => m.Name == "SyncOnePlayer");
        var nativeMethods = native.Methods.Where(m => m.Name is "SyncOnePlayer" or "SyncOnePlayer_ItemArray" or "SyncConnectedPlayer").ToArray();
        var mod = probe.MainModule.Types.Single(t => t.Name == "VirtualPlayerEngineProbe");
        var session = probe.MainModule.Types.Single(t => t.Name == "ProbeSession");
        var adapter = probe.MainModule.Types.Single(t => t.Name == "NetworkStateSync");
        var debug = probe.MainModule.Types.Single(t => t.Name == "ProbeDebugAppearance");
        var noticeType = probe.MainModule.Types.Single(t => t.Name == "ProbeAnnouncements");
        Check(adapter.Methods.All(m => !m.HasBody || !m.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld
            || i.Operand is MethodReference r && r.Name is "Configure" or "SetDefaults")),
            "sync adapter has no character/item/appearance writes or initialization calls");
        var spawn = session.Methods.Single(m => m.Name == "Spawn");
        Check(!spawn.Body.Instructions.Any(i => i.Operand is MethodReference m
                && (m.DeclaringType.FullName == debug.FullName || m.Name is "Configure" or "SetDefaults"))
            && !spawn.Body.Instructions.Any(i => i.Operand is FieldReference f
                && f.Name is "inventory" or "armor" or "shirtColor" or "hairColor" or "hair" or "skinVariant"),
            "default Spawn never applies debug appearance, equipment or inventory initialization");
        var policy = mod.Methods.Single(m => m.Name == "IsNetworkActor");
        var engineNetplay = game.MainModule.Types.Single(t => t.FullName == "Terraria.Netplay");
        var engineMethods = game.MainModule.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
        string[] NativeCallers(string type,string name) => engineMethods.Where(m => m.Body.Instructions.Any(i => i.Operand is MethodReference r
            && r.DeclaringType.FullName==type && r.Name==name)).Select(m=>m.FullName).OrderBy(x=>x).ToArray();
        Check(NativeCallers("Terraria.MessageBuffer","GetData").SequenceEqual(new[]{"System.Void Terraria.NetMessage::CheckBytes(System.Int32)"}),
            "all installed GetData calls pass through native buffer-locked CheckBytes");
        Check(NativeCallers("Terraria.Netplay","UpdateServerInMainThread").SequenceEqual(new[]{"System.Void Terraria.Netplay::UpdateInMainThread()"})
            && engineNetplay.Methods.Single(m=>m.Name=="UpdateServerInMainThread").Body.Instructions.Any(i=>i.Operand is MethodReference r
                && r.DeclaringType.FullName=="Terraria.NetMessage" && r.Name=="CheckBytes"),
            "main-thread network hook encloses native server CheckBytes before it takes message-buffer locks");
        var controlBody = native.Methods.Single(m => m.Name == "SendData").Body.Instructions
            .Where(i => i.Offset >= 0x10e2 && i.Offset < 0x1378).ToArray();
        foreach (string field in new[] { "position", "velocity", "direction", "controlLeft", "controlRight", "controlJump", "controlUseItem", "selectedItem" })
            Check(controlBody.Any(i => i.Operand is FieldReference f && f.Name == field), "installed native PlayerControls serializer includes " + field);
        var support = mod.Methods.Where(m => m.Name is "IsNetworkActor" or "OwnedWithoutConnection" or "SyncOne" or "MainNetworkTick" or "NetworkTick")
            .Concat(session.Methods.Where(m => m.Name == "Quarantine"))
            .Concat(adapter.Methods.Where(m => m.HasBody && m.Name != ".cctor"))
            .Concat(debug.Methods.Where(m => m.HasBody))
            .Concat(noticeType.Methods.Where(m => m.HasBody)).ToArray();

        var baseline = Compile(nativeMethods, "SyncBaseline");
        int originalCount = one.Body.Instructions.Count;
        using (var context = new ILContext(one)) context.Invoke(il => EnginePatches.PlayerSync(il.Method, il.Module.ImportReference(policy)));
        Check(one.Body.Instructions.Count == originalCount + 8, "only eight IL instructions added around native payload");
        Check(!one.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "State"),
            "native connection State is never overwritten");
        try { EnginePatches.PlayerSync(one, one.Module.ImportReference(policy)); throw new Exception("duplicate accepted"); }
        catch (InvalidOperationException) { Check(true, "duplicate patch safely rejected"); }
        // An actual reflection/DMD/ILContext path, not only raw Cecil.
        var runtime = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(gamePath, "tModLoader.dll"));
        using (var dmd = new DynamicMethodDefinition(runtime.GetType("Terraria.NetMessage").GetMethod("SyncOnePlayer", BindingFlags.NonPublic | BindingFlags.Static)))
        using (var context = new ILContext(dmd.Definition))
        {
            context.Invoke(il => EnginePatches.PlayerSync(il.Method, il.Module.ImportReference(policy)));
            Check(dmd.Definition.Body.Variables.Count == 5, "real DMD/MonoMod labels accept the strict G2 patch");
        }
        foreach (string mutation in new[] { "guard", "boundary", "disconnect", "signature" })
        {
            using var altered = AssemblyDefinition.ReadAssembly(Path.Combine(gamePath, "tModLoader.dll"));
            var method = altered.MainModule.Types.Single(t => t.FullName == "Terraria.NetMessage").Methods.Single(m => m.Name == "SyncOnePlayer");
            if (mutation == "guard") method.Body.Instructions.Single(i => i.Offset == 0x20).OpCode = OpCodes.Beq;
            else if (mutation == "boundary") method.Body.Instructions.Single(i => i.Offset == 0x36a).Operand = policy;
            else if (mutation == "disconnect") method.Body.Instructions.Single(i => i.Offset == 0x4af).Operand = policy;
            else method.Parameters[0].ParameterType = altered.MainModule.TypeSystem.Int64;
            int before = method.Body.Instructions.Count, locals = method.Body.Variables.Count;
            try { EnginePatches.PlayerSync(method, method.Module.ImportReference(policy)); throw new Exception("bad shape accepted"); }
            catch (InvalidOperationException) { Check(method.Body.Instructions.Count == before && method.Body.Variables.Count == locals,
                "unsupported IL fails before mutation: " + mutation); }
        }
        var fixture = Compile(nativeMethods.Concat(support).ToArray(), "SyncPatched");
        var raw = (Action<int,int,int>)fixture.GetMethod("SyncOnePlayer").CreateDelegate(typeof(Action<int,int,int>));
        var wrapper = (Action<object,Action<int,int,int>,int,int,int>)fixture.GetMethod("SyncOne")
            .CreateDelegate(typeof(Action<object,Action<int,int,int>,int,int,int>));
        Effects.InvokePatched = (id, remote, ignore) => wrapper(null, raw, id, remote, ignore);
        var original = (Action<int,int,int>)baseline.GetMethod("SyncOnePlayer").CreateDelegate(typeof(Action<int,int,int>));
        var lateJoin = (Action<int>)fixture.GetMethod("SyncConnectedPlayer").CreateDelegate(typeof(Action<int>));
        var full = (Action<PlayerState>)fixture.GetMethod("Full").CreateDelegate(typeof(Action<PlayerState>));
        var tick = (Action<PlayerState>)fixture.GetMethod("Tick").CreateDelegate(typeof(Action<PlayerState>));
        var style = (Action<PlayerState>)fixture.GetMethod("Toggle").CreateDelegate(typeof(Action<PlayerState>));
        var clearDebug = (Action)fixture.GetMethod("Clear").CreateDelegate(typeof(Action));
        var joinNotice = (Action<PlayerState>)fixture.GetMethod("Join").CreateDelegate(typeof(Action<PlayerState>));
        var leftNotice = (Action<PlayerState>)fixture.GetMethod("Left").CreateDelegate(typeof(Action<PlayerState>));
        var resetNotices = (Action)fixture.GetMethod("ResetNotices").CreateDelegate(typeof(Action));
        var networkPass = (Action<object,Action>)fixture.GetMethod("NetworkTick").CreateDelegate(typeof(Action<object,Action>));
        var mainNetworkPass = (Action<object,Action>)fixture.GetMethod("MainNetworkTick").CreateDelegate(typeof(Action<object,Action>));
        var leave = (Action<PlayerState>)fixture.GetMethod("Leave").CreateDelegate(typeof(Action<PlayerState>));
        var owns = (Func<int,bool>)fixture.GetMethod("IsNetworkActor").CreateDelegate(typeof(Func<int,bool>));
        var resetAdapter = (Action)fixture.GetMethod("Reset").CreateDelegate(typeof(Action));
        void Reset()
        {
            MainState.player = Enumerable.Range(0,256).Select(i => new PlayerState { whoAmI=i }).ToArray();
            NetState.Clients = Enumerable.Range(0,256).Select(i => new RemoteState { Id=i }).ToArray();
            MainState.netMode = 2; MainState.GameUpdateCount = 0;
            Effects.Reservation = new(); Effects.Reservation.ArmBeforeListening(false,4);
            Effects.Actor = MainState.player[3]; Effects.Actor.active = true;
            Effects.Ready = Effects.EngineMainThread = true; Effects.Quarantined = false; Effects.Fault = Effects.Phase = null;
            Effects.Packets.Clear(); Effects.Announcements = Effects.Disconnects = Effects.ModPayloads = 0;
            Effects.Notices.Clear(); Effects.ThrowNotice=false;
            Effects.OnPacket = null; Effects.ThrowPacket = -1; resetAdapter(); clearDebug(); resetNotices();
        }
        foreach (int state in new[] { 0, 10 }) foreach (bool active in new[] { false, true })
        {
            Reset(); NetState.Clients[0].State=state; MainState.player[0].active=active;
            original(0,7,8); var packets = Effects.Packets.ToArray(); var announcements=Effects.Announcements; var disconnects=Effects.Disconnects;
            Reset(); NetState.Clients[0].State=state; MainState.player[0].active=active;
            Effects.InvokePatched(0,7,8);
            Check(Effects.Packets.SequenceEqual(packets) && Effects.Announcements==announcements && Effects.Disconnects==disconnects,
                $"complete human native sync unchanged: State={state}, active={active}");
        }
        foreach (int state in new[] { 0, 10 })
        {
            void UnexpectedHuman()
            {
                Reset(); MainState.player[3]=new PlayerState { whoAmI=3, active=true };
                NetState.Clients[3].State=state;
                if(state==10) NetState.Clients[3].Socket=new EngineHookOfflineTests.SocketState();
            }
            UnexpectedHuman(); original(3,7,8); var nativePackets=Effects.Packets.ToArray(); var announcements=Effects.Announcements; var disconnects=Effects.Disconnects;
            UnexpectedHuman(); Effects.InvokePatched(3,7,8);
            Check(Effects.Packets.SequenceEqual(nativePackets) && Effects.Announcements==announcements && Effects.Disconnects==disconnects
                && Effects.Quarantined && Effects.Reservation.IsReserved(3),
                "unexpected human/replacement in reserved slot retains complete native sync path: State="+state);
        }
        Reset(); full(Effects.Actor);
        Check(Effects.Packets.Any(p=>p.Type==14 && p.Value==1) && Effects.Packets.Any(p=>p.Type==4)
            && Effects.Packets.Any(p=>p.Type==13) && Effects.Packets.Any(p=>p.Type==5)
            && Effects.Packets.Any(p=>p.Type==16) && Effects.Packets.All(p=>p.Slot==3 && p.Remote==-1 && p.Ignore==-1),
            "actual native AI full payload includes identity/active/controls/equipment/life with broadcast routing");
        Check(Effects.FullSnapshots==1 && Effects.ModPayloads==1 && Effects.Announcements==0 && Effects.Disconnects==0
            && !NetState.Clients[3].IsAnnouncementCompleted && NetState.Clients[3].State==0,
            "AI returns after native ModPlayer payload without changing connection or human announcements");
        Reset(); MainState.player[0].active=true; NetState.Clients[0].State=10;
        lateJoin(0);
        Check(Effects.Packets.Any(p=>p.Slot==3 && p.Type==4) && Effects.Packets.Where(p=>p.Slot==3).All(p=>p.Remote==0 && p.Ignore==-1),
            "complete native late-join SyncConnectedPlayer routes AI snapshot to joining client");
        foreach (string invalid in new[] { "no reservation", "no actor", "replacement", "wrong identity", "connected", "active connection", "pending", "approved", "handshake", "unknown client", "quarantined", "fault", "not server" })
        {
            Reset();
            if(invalid=="no reservation") Effects.Reservation.Release();
            else if(invalid=="no actor") Effects.Actor=null;
            else if(invalid=="replacement") MainState.player[3]=new PlayerState { whoAmI=3, active=true };
            else if(invalid=="wrong identity") Effects.Actor.whoAmI=2;
            else if(invalid=="connected") NetState.Clients[3].Socket=new EngineHookOfflineTests.SocketState();
            else if(invalid=="active connection") NetState.Clients[3].IsActive=true;
            else if(invalid=="pending") NetState.Clients[3].PendingTermination=true;
            else if(invalid=="approved") NetState.Clients[3].PendingTerminationApproved=true;
            else if(invalid=="handshake") NetState.Clients[3].State=1;
            else if(invalid=="unknown client") NetState.Clients[3]=null;
            else if(invalid=="quarantined") Effects.Quarantined=true;
            else if(invalid=="fault") Effects.Fault="stopped";
            else MainState.netMode=1;
            Check(!owns(3) && Effects.Packets.Count==0, "actual AI eligibility rejects " + invalid);
        }
        Reset(); full(Effects.Actor); Effects.Packets.Clear(); MainState.GameUpdateCount=5; tick(Effects.Actor);
        Check(Effects.Packets.Count==0, "controls are rate-limited below six game ticks");
        MainState.GameUpdateCount=6; tick(Effects.Actor);
        Check(Effects.Packets.Count==1 && Effects.Packets[0].Type==13 && Effects.Controls==1, "native movement/control packet sent after six ticks");
        MainState.GameUpdateCount=300; tick(Effects.Actor);
        Check(Effects.FullSnapshots==2 && Effects.Packets.Any(p=>p.Type==5), "bounded native full refresh republishes equipment/appearance");
        ItemState[] CharacterItems(PlayerState p) => p.inventory.Concat(p.armor).Concat(p.dye).Concat(p.miscEquips).Concat(p.miscDyes)
            .Concat(p.Loadouts.SelectMany(l => l.Armor.Concat(l.Dye))).ToArray();
        void SeedCharacter(PlayerState p)
        {
            p.name="Existing character"; p.hair=17; p.skinVariant=3; p.selectedItem=8;
            p.hairColor=new(1,2,3); p.shirtColor=new(11,12,13); p.underShirtColor=new(21,22,23);
            p.pantsColor=new(31,32,33); p.shoeColor=new(41,42,43); p.eyeColor=new(51,52,53); p.skinColor=new(61,62,63);
            int n=0;
            foreach(var item in CharacterItems(p)) { item.type=1000+n; item.stack=1+n; item.prefix=n%8; item.customData="saved-"+n++; }
        }
        string CharacterSnapshot(PlayerState p) => JsonSerializer.Serialize(new { p.name,p.hair,p.skinVariant,p.selectedItem,
            colors=new[]{p.hairColor,p.shirtColor,p.underShirtColor,p.pantsColor,p.shoeColor,p.eyeColor,p.skinColor},
            items=CharacterItems(p).Select(i=>new{i.type,i.stack,i.prefix,i.customData}).ToArray()
        },new JsonSerializerOptions{IncludeFields=true});
        Reset(); SeedCharacter(Effects.Actor); var originalData=CharacterSnapshot(Effects.Actor); var originalItems=CharacterItems(Effects.Actor);
        void CharacterUnchanged(string operation) => Check(CharacterSnapshot(Effects.Actor)==originalData
            && CharacterItems(Effects.Actor).SequenceEqual(originalItems), "existing character data and Item instances preserved by "+operation);
        full(Effects.Actor); CharacterUnchanged("initial full sync");
        full(Effects.Actor); CharacterUnchanged("explicit resync");
        MainState.GameUpdateCount=6; tick(Effects.Actor); CharacterUnchanged("control sync");
        MainState.GameUpdateCount=300; tick(Effects.Actor); CharacterUnchanged("periodic equipment/appearance refresh");
        MainState.player[0].active=true; NetState.Clients[0].State=10; lateJoin(0); CharacterUnchanged("native late-join sync");
        leave(Effects.Actor); CharacterUnchanged("departure sync");
        Reset(); SeedCharacter(Effects.Actor); originalData=CharacterSnapshot(Effects.Actor); var savedHead=Effects.Actor.armor[0];
        style(Effects.Actor); var overlayHead=Effects.Actor.armor[0];
        Check(!ReferenceEquals(overlayHead,savedHead) && savedHead.type==1059 && savedHead.customData=="saved-59"
            && Effects.Actor.inventory[0].type==1000 && Effects.Actor.selectedItem==8,
            "explicit debug overlay preserves original helmet data and never grants/resets inventory items");
        full(Effects.Actor);
        Check(ReferenceEquals(Effects.Actor.armor[0],overlayHead) && Effects.Actor.shirtColor.R==190,
            "resync preserves explicitly selected debug appearance without reinitialization");
        style(Effects.Actor);
        Check(Effects.FullSnapshots==3 && ReferenceEquals(Effects.Actor.armor[0],savedHead) && CharacterSnapshot(Effects.Actor)==originalData,
            "second debug toggle restores exact original equipment instance and appearance");
        style(Effects.Actor); var externalHead=Effects.Actor.armor[0]=new ItemState{type=7777,customData="external"}; int packetsBefore=Effects.Packets.Count;
        try { style(Effects.Actor); throw new Exception("external equipment overwritten"); } catch(InvalidOperationException) { }
        Check(ReferenceEquals(Effects.Actor.armor[0],externalHead) && Effects.Packets.Count==packetsBefore,
            "debug restore refuses to overwrite externally replaced equipment");
        Reset(); full(Effects.Actor); Effects.Packets.Clear(); Effects.ThrowPacket=13; MainState.GameUpdateCount=6;
        try { tick(Effects.Actor); throw new Exception("controls failure swallowed"); } catch(IOException) { }
        Effects.ThrowPacket=-1;
        try { tick(Effects.Actor); throw new Exception("failed sync resumed"); } catch(InvalidOperationException) { }
        Check(Effects.Fault!=null && Effects.Packets.Count==0 && Effects.Actor.active && Effects.Reservation.IsReserved(3),
            "control-send failure stops future publication while retaining actor for safe lifecycle cleanup");
        Reset(); full(Effects.Actor); Effects.Packets.Clear(); leave(Effects.Actor); leave(Effects.Actor);
        Check(Effects.Packets.Count==1 && Effects.Packets[0].Type==14 && Effects.Packets[0].Value==0
            && Effects.Packets[0].Slot==3 && Effects.Departures==1 && Effects.Actor.active && Effects.Reservation.IsReserved(3),
            "departure sends exactly one AI inactive packet before lifecycle writes/releases the lease");
        Reset(); Effects.ThrowPacket=4;
        try { full(Effects.Actor); throw new Exception("transport failure swallowed"); } catch (TargetInvocationException) { }
        Check(Effects.Fault!=null && Effects.possiblyVisible && Effects.Reservation.IsReserved(3) && Effects.Actor.active,
            "partial initial publication records terminal fault and retains ownership for rollback");
        Effects.ThrowPacket=-1; leave(Effects.Actor);
        Check(Effects.Departures==1 && Effects.Packets.Last().Type==14 && Effects.Packets.Last().Value==0,
            "safe departure compensates for partially sent initial snapshot despite terminal sync fault");
        Reset(); full(Effects.Actor); Effects.ThrowPacket=14;
        try { leave(Effects.Actor); throw new Exception("departure failure swallowed"); } catch(IOException) { }
        Check(Effects.Quarantined && Effects.Reservation.IsReserved(3) && Effects.Actor.active,
            "failed departure quarantines without changing AI Player or releasing slot");
        Reset(); full(Effects.Actor); var unknown=MainState.player[3]=new PlayerState { active=true,whoAmI=3 };
        int sent=Effects.Packets.Count;
        try { leave(Effects.Actor); throw new Exception("unknown player accepted"); } catch(InvalidOperationException) { }
        Check(Effects.Quarantined && Effects.Packets.Count==sent && unknown.active && Effects.Reservation.IsReserved(3),
            "ownership loss forbids departure packet for unknown Player and retains reservation");
        Reset(); Exception threadError=null;
        var worker=new Thread(()=>{try{full(Effects.Actor);}catch(Exception e){threadError=e;}}); worker.Start(); worker.Join();
        Check(threadError is InvalidOperationException && Effects.Packets.Count==0, "actual sync rejects foreign-thread publication before any packet");
        Reset(); Effects.Actor.name="Notice probe"; full(Effects.Actor); joinNotice(Effects.Actor); joinNotice(Effects.Actor);
        Check(Effects.Notices.Count==1 && Effects.JoinNotices==1 && Effects.Notices[0]==new Notice(LangState.mp[19].Key,"Notice probe",255,240,20,3),
            "successful AI join uses native localized yellow announcement exactly once");
        full(Effects.Actor); MainState.GameUpdateCount=300; tick(Effects.Actor);
        MainState.player[0].active=true; NetState.Clients[0].State=10; lateJoin(0);
        Check(Effects.Notices.Count(n=>n.Name=="Notice probe")==1 && Effects.JoinNotices==1 && !NetState.Clients[3].IsAnnouncementCompleted,
            "resync, periodic refresh and late join never repeat AI join notice or write native announcement flags");
        leave(Effects.Actor); Effects.Actor.active=false; leftNotice(Effects.Actor); leftNotice(Effects.Actor);
        Check(Effects.LeaveNotices==1 && Effects.Notices.Last()==new Notice(LangState.mp[20].Key,"Notice probe",255,240,20,3),
            "confirmed AI teardown uses native localized yellow leave announcement exactly once");
        Reset(); Effects.ThrowPacket=4;
        try{full(Effects.Actor);}catch(TargetInvocationException){}
        Effects.ThrowPacket=-1; leave(Effects.Actor); leftNotice(Effects.Actor);
        Check(Effects.Notices.Count==0 && Effects.JoinNotices==0 && Effects.LeaveNotices==0,
            "failed initial publication and its rollback never announce a successful join or leave");
        Reset(); full(Effects.Actor); joinNotice(Effects.Actor); var replacement=MainState.player[3]=new PlayerState{whoAmI=3,active=true};
        try{leftNotice(Effects.Actor);throw new Exception("unknown actor announced");}catch(InvalidOperationException){}
        Check(Effects.Notices.Count==1 && Effects.LeaveNotices==0 && replacement.active && Effects.Reservation.IsReserved(3),
            "lost AI ownership prevents leave announcement for unknown Player");
        Reset(); full(Effects.Actor); Effects.ThrowNotice=true; joinNotice(Effects.Actor); Effects.ThrowNotice=false; joinNotice(Effects.Actor);
        Check(Effects.Notices.Count==1 && Effects.NoticeFailures==1 && Effects.Fault==null && !Effects.Quarantined && Effects.Actor.active,
            "partially failing join chat is not retried and never faults or rolls back valid AI");
        leave(Effects.Actor); Effects.Actor.active=false; Effects.ThrowNotice=true; leftNotice(Effects.Actor); Effects.ThrowNotice=false; leftNotice(Effects.Actor);
        Check(Effects.Notices.Count==2 && Effects.NoticeFailures==2 && Effects.Fault==null && !Effects.Quarantined && Effects.Departures==1,
            "failing leave chat is not retried and never blocks successful Player departure");
        // Model the captured deadlock: main holds CheckBytes' buffer; network
        // holds Gate and its synchronous receive callback needs that buffer.
        // TryEnter bounds the regression: the old predicate fails, never hangs.
        Reset(); var messageBuffer = new object();
        using var gateHeld = new ManualResetEventSlim(); using var receiveReady = new ManualResetEventSlim();
        bool received=false; Exception receiveError=null;
        var inlineReceive = new Thread(()=>{
            try { networkPass(null,()=>{
                gateHeld.Set(); if(!receiveReady.Wait(2000))throw new Exception("main did not reach buffer");
                if(Monitor.TryEnter(messageBuffer,500)){try{received=true;}finally{Monitor.Exit(messageBuffer);}}
            }); } catch(Exception e){receiveError=e;}
        }) { IsBackground=true };
        bool humanOwned;
        lock(messageBuffer)
        {
            inlineReceive.Start(); if(!gateHeld.Wait(2000))throw new Exception("network did not hold Gate");
            receiveReady.Set(); humanOwned=owns(0);
        }
        Check(inlineReceive.Join(2000) && receiveError==null && received && !humanOwned,
            "actual human eligibility never waits for Gate while native main thread holds a message buffer");
        Reset(); MainState.player[0].active=true; NetState.Clients[0].State=10;
        using var workerStarted = new ManualResetEventSlim(); using var nativeNetworkEntered = new ManualResetEventSlim();
        bool receiveAfterMain=false, networkExcluded=false; Exception orderedError=null;
        var orderedReceive = new Thread(()=>{
            workerStarted.Set();
            try { networkPass(null,()=>{
                nativeNetworkEntered.Set();
                if(Monitor.TryEnter(messageBuffer,500)){try{receiveAfterMain=true;}finally{Monitor.Exit(messageBuffer);}}
            }); }catch(Exception e){orderedError=e;}
        }) { IsBackground=true };
        mainNetworkPass(null,()=>{
            lock(messageBuffer)
            {
                orderedReceive.Start(); if(!workerStarted.Wait(2000))throw new Exception("receive worker did not start");
                networkExcluded=!nativeNetworkEntered.Wait(60);
                lateJoin(0); // Includes the actual patched AI predicate under buffer + Gate.
            }
        });
        Check(orderedReceive.Join(2000) && orderedError==null && networkExcluded && receiveAfterMain
            && Effects.Packets.Any(p=>p.Slot==3 && p.Remote==0),
            "actual main-network wrapper orders Gate before buffer and completes AI late-join with concurrent inline receive");
        Reset(); Exception mainThreadError=null; bool foreignOrig=false;
        var wrongMain = new Thread(()=>{try{mainNetworkPass(null,()=>foreignOrig=true);}catch(Exception e){mainThreadError=e;}});
        wrongMain.Start(); wrongMain.Join();
        Check(mainThreadError is InvalidOperationException && !foreignOrig,"main network scope rejects wrong simulation thread before native work");
        Effects.Reservation.Release(); bool noReservationOrig=false; mainNetworkPass(null,()=>noReservationOrig=true);
        Check(noReservationOrig,"absent/released reservation preserves native main-network processing");
        Console.WriteLine($"G2NetworkSync: {checks} targeted checks passed; SINGLE-WINDOW LIVE NOT RUN; REMOTE MULTIPLAYER LIVE NOT RUN.");
        return checks;
    }

    private static Type Compile(MethodDefinition[] sources, string name)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name,new Version(1,0)),name,ModuleKind.Dll);
        var module=assembly.MainModule;
        var map=new Dictionary<string,Type> {
            ["Terraria.Main"]=typeof(MainState), ["Terraria.Netplay"]=typeof(NetState), ["Terraria.Player"]=typeof(PlayerState),
            ["Terraria.Entity"]=typeof(EngineHookOfflineTests.EntityState), ["Terraria.RemoteClient"]=typeof(RemoteState),
            ["Terraria.Item"]=typeof(ItemState), ["Terraria.EquipmentLoadout"]=typeof(LoadoutState),
            ["Terraria.ID.PlayerItemSlotID"]=typeof(ItemSlots), ["Terraria.Lang"]=typeof(LangState),
            ["Terraria.Localization.LocalizedText"]=typeof(LocalizedState), ["Microsoft.Xna.Framework.Color"]=typeof(ColorState),
            ["Terraria.Localization.NetworkText"]=typeof(EngineHookOfflineTests.TextState),
            ["Terraria.NetMessage"]=typeof(Effects), ["Terraria.ModLoader.PlayerLoader"]=typeof(Effects),
            ["Terraria.Player/Hooks"]=typeof(Effects), ["Terraria.Chat.ChatHelper"]=typeof(Effects),
            ["Terraria.ModLoader.Logging"]=typeof(Effects), ["Terraria.GameContent.Events.CreditsRollEvent"]=typeof(Effects),
            ["Terraria.NPC"]=typeof(NpcState), ["Terraria.GameContent.CoinLossRevengeSystem"]=typeof(RevengeState),
            ["Terraria.Program"]=typeof(Effects), ["VirtualPlayerEngineProbe.VirtualPlayerEngineProbe"]=typeof(Effects),
            ["VirtualPlayerEngineProbe.ProbeSession"]=typeof(Effects), ["VirtualPlayerEngineProbe.NetworkStateSync"]=typeof(Effects),
            ["VirtualPlayerEngineProbe.NativeSurvival"]=typeof(Effects),
            ["VirtualPlayerEngineProbe.ProbeDebugAppearance"]=typeof(Effects),
            ["VirtualPlayerEngineProbe.ProbeAnnouncements"]=typeof(Effects),
            ["VirtualPlayerEngineProbe.StartupReservation"]=typeof(StartupReservation),
            ["VirtualPlayerEngineProbe.StartupReservation/View"]=typeof(StartupReservation.View),
            ["Terraria.On_NetMessage/orig_SyncOnePlayer"]=typeof(Action<int,int,int>),
            ["Terraria.On_Netplay/orig_UpdateInMainThread"]=typeof(Action),
            ["Terraria.On_Netplay/orig_UpdateConnectedClients"]=typeof(Action)
        };
        TypeReference TypeRef(TypeReference t)=>t is ArrayType a ? new ArrayType(TypeRef(a.ElementType))
            : map.TryGetValue(t.FullName,out var type)?module.ImportReference(type):module.ImportReference(t);
        var runner=new TypeDefinition("Fixture",name,Mono.Cecil.TypeAttributes.Public|Mono.Cecil.TypeAttributes.Abstract|Mono.Cecil.TypeAttributes.Sealed,module.TypeSystem.Object);
        module.Types.Add(runner);
        var copies=sources.ToDictionary(m=>m.FullName,m=>new MethodDefinition(m.Name,Mono.Cecil.MethodAttributes.Public|Mono.Cecil.MethodAttributes.Static,TypeRef(m.ReturnType)));
        foreach(var source in sources)
        {
            var dst=copies[source.FullName]; runner.Methods.Add(dst);
            if(source.HasThis) dst.Parameters.Add(new ParameterDefinition("unusedThis",Mono.Cecil.ParameterAttributes.None,module.TypeSystem.Object));
            foreach(var p in source.Parameters) dst.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,TypeRef(p.ParameterType)));
            dst.Body.InitLocals=true; dst.Body.MaxStackSize=source.Body.MaxStackSize;
            foreach(var v in source.Body.Variables) dst.Body.Variables.Add(new VariableDefinition(TypeRef(v.VariableType)));
            var code=source.Body.Instructions.ToDictionary(i=>i,_=>Instruction.Create(OpCodes.Nop));
            object Import(object o)=>o switch {
                Instruction i=>code[i], Instruction[] b=>b.Select(i=>code[i]).ToArray(), VariableDefinition v=>dst.Body.Variables[v.Index],
                ParameterDefinition p=>dst.Parameters[p.Index+(source.HasThis?1:0)],
                MethodReference m when copies.TryGetValue(m.FullName,out var copy)=>copy,
                MethodReference m when m.Name==".ctor" && map.TryGetValue(m.DeclaringType.FullName,out var type)=>module.ImportReference(type.GetConstructors().Single(c=>c.GetParameters().Length==m.Parameters.Count)),
                MethodReference m when map.TryGetValue(m.DeclaringType.FullName,out var type)=>module.ImportReference(type.GetMethods().SingleOrDefault(x=>x.Name==m.Name && x.GetParameters().Length==m.Parameters.Count)
                    ?? throw new Exception("Missing fixture method "+m.FullName)),
                FieldReference f when map.TryGetValue(f.DeclaringType.FullName,out var type)=>module.ImportReference(type.GetField(f.Name)??throw new Exception("Missing fixture field "+f.FullName)),
                TypeReference t=>TypeRef(t), FieldReference f=>module.ImportReference(f), MethodReference m=>module.ImportReference(m), _=>o
            };
            foreach(var i in source.Body.Instructions){code[i].OpCode=i.OpCode;code[i].Operand=Import(i.Operand);dst.Body.Instructions.Add(code[i]);}
            foreach(var e in source.Body.ExceptionHandlers) dst.Body.ExceptionHandlers.Add(new ExceptionHandler(e.HandlerType){
                TryStart=code[e.TryStart],TryEnd=e.TryEnd==null?null:code[e.TryEnd],HandlerStart=code[e.HandlerStart],
                HandlerEnd=e.HandlerEnd==null?null:code[e.HandlerEnd],CatchType=e.CatchType==null?null:TypeRef(e.CatchType)});
        }
        using var stream=new MemoryStream(); assembly.Write(stream); stream.Position=0;
        return AssemblyLoadContext.Default.LoadFromStream(stream).GetType("Fixture."+name);
    }
}
