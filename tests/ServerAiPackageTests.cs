using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

public static class ServerAiPackageTests
{
    public static void Run(string package, string expectedVersion)
    {
        int checks=0;
        void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;Console.WriteLine("PASS AI package: "+text);}
        using var file=File.OpenRead(package);using var reader=new BinaryReader(file);
        Check(System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4))=="TMOD","real TMOD package");
        reader.ReadString();reader.ReadBytes(276);reader.ReadInt32();
        Check(reader.ReadString()=="CompanionBridge"&&reader.ReadString()==expectedVersion,"formal package identity/version");
        int count=reader.ReadInt32(),offset=0,dllOffset=-1,size=0,packed=0;
        for(int i=0;i<count;i++){string name=reader.ReadString();int length=reader.ReadInt32(),compressed=reader.ReadInt32();if(name=="CompanionBridge.dll"){dllOffset=offset;size=length;packed=compressed;}offset+=compressed;}
        Check(dllOffset>=0,"formal DLL packaged");file.Position+=dllOffset;
        using var payload=new MemoryStream(reader.ReadBytes(packed));using var bytes=new MemoryStream();
        if(packed==size)payload.CopyTo(bytes);else {using var inflate=new System.IO.Compression.DeflateStream(payload,System.IO.Compression.CompressionMode.Decompress);inflate.CopyTo(bytes);}
        bytes.Position=0;using var assembly=AssemblyDefinition.ReadAssembly(bytes);
        IEnumerable<TypeDefinition> Walk(TypeDefinition t)=>new[]{t}.Concat(t.NestedTypes.SelectMany(Walk));
        var types=assembly.MainModule.Types.SelectMany(Walk).ToArray();
        TypeDefinition Type(string name)=>types.Single(t=>t.Name==name);
        bool Calls(MethodDefinition m,string name)=>m.HasBody&&m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.Name==name);
        var direct=Type("ServerAiDirectInput");
        Check(direct.Interfaces.Any(i=>i.InterfaceType.Name=="IMovementDriver"),"direct input uses the existing movement owner boundary");
        Check(!direct.Methods.Where(m=>m.HasBody).Any(m=>Calls(m,"Predict")||Calls(m,"Plan")||Calls(m,"Healthy")),"direct input has no planner/profile/grounded start gate");
        Check(!direct.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference f&&f.Name is "position" or "velocity"),"direct input never assigns native position or velocity");
        Check(Calls(direct.Methods.Single(m=>m.Name=="Update"),"Invoke")&&Calls(direct.Methods.Single(m=>m.Name=="Update"),"Release"),"direct input calls original natural update and releases in cleanup");
        Check(Calls(Type("ServerAiNativeActions").Methods.Single(m=>m.Name=="Start"),"StartCore")&&Calls(Type("ServerAiNativeActions").Methods.Single(m=>m.Name=="StartCore"),"Start")&&Type("AiAtomicActions").Methods.Any(m=>m.HasBody&&m.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldstr&&(string)i.Operand=="move_input")),"direct input is in the packaged atomic protocol");
        var finish=Type("ProbeSession").Methods.Single(m=>m.Name=="Finish");
        int CallIndex(string type,string name)=>finish.Body.Instructions.ToList().FindIndex(i=>i.Operand is MethodReference r&&r.DeclaringType.Name==type&&r.Name==name);
        Check(CallIndex("WorldCharacterPersistence","BeforeRetire")>CallIndex("ActorControl","Retire")
            &&CallIndex("WorldCharacterPersistence","BeforeRetire")>CallIndex("ActorItemUse","Retire")
            &&CallIndex("WorldCharacterPersistence","BeforeRetire")>CallIndex("ActorMining","Retire")
            &&CallIndex("WorldCharacterPersistence","BeforeRetire")<CallIndex("NetworkStateSync","Leave"),"world save follows native action/input retirement and precedes actor departure");
        Check(Calls(Type("ServerAiSystem").Methods.Single(m=>m.Name=="Pump"),"Start")
            &&Type("ServerAiSystem").Methods.Single(m=>m.Name=="Capture").Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.DeclaringType.Name=="WorldCharacterPersistence"&&r.Name=="get_Ready"),"IPC actor identity requires a restored and committed world profile");
        Check(types.Where(t=>t.FullName.StartsWith(Type("CharacterStore").FullName)).SelectMany(t=>t.Methods).Any(m=>Calls(m,"SavePlayer"))
            &&Calls(Type("CharacterStore").Methods.Single(m=>m.Name=="SaveCore"),"Run")
            &&Calls(Type("CharacterStore").Methods.Single(m=>m.Name=="SaveCore"),"Read"),"world and legacy saves share native SavePlayer and native read-back validation");
        Check(Calls(Type("ServerAiNavigation").Methods.Single(m=>m.Name=="Stop"),"Observe")
            &&Type("ServerAiNavigation").Methods.Single(m=>m.Name=="Stop").Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldstr&&(string)i.Operand=="native_navigation_landing_unconfirmed"),"native airborne failure cleanup reports unconfirmed landing");
        Check(Walk(Type("StateSpacePlanner")).SelectMany(t=>t.Methods).Where(m=>m.HasBody)
            .Any(m=>Calls(m,"SupportedCheckpoint")),"planner requires real supported checkpoints beyond collision contact");
        var motionWorld=Type("NativeMotionWorld");
        Check(Calls(motionWorld.Methods.Single(m=>m.Name=="MoveCore"),"Horizontal"),"navigation contacts use native clipped movement rather than nearby tile scan");
        Check(Calls(motionWorld.Methods.Single(m=>m.Name=="Observe"),"ContactRejection")
            &&Calls(motionWorld.Methods.Single(m=>m.Name=="LandingRejection"),"ContactRejection")
            &&Calls(motionWorld.Methods.Single(m=>m.Name=="ContactRejection"),"MoveCore"),
            "actual grounded observation and predicted support reuse native collision");
        Check(Calls(Type("ServerSceneReader").Methods.Single(m=>m.Name=="Read"),"FromPixels"),"scene body tile bounds use host collision rectangle");
        Check(Calls(Type("ServerAiNativeActions").Methods.Single(m=>m.Name=="Poll"),"Mining")
            &&Type("AiActionReasons").Methods.Single(m=>m.Name=="Mining").Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldstr&&(string)i.Operand=="native_tile_destroyed"),"mining receipt translates native failure enum to outcome reason");
        Check(Calls(Type("AiAtomicActions").Methods.Single(m=>m.Name=="End"),"get_ReasonDetail"),"terminal serialization retains native diagnostic detail separately");
        var characterStore=Type("CharacterStore");
        Check(Calls(characterStore.Methods.Single(m=>m.Name=="CreateNewCandidate"),"Run"),"new character construction uses native character isolation scope");
        Check(!Calls(characterStore.Methods.Single(m=>m.Name=="Load"),"CreateNewCandidate"),"persisted character load never grants starter items");
        Check(!Type("NativeSurvival").Methods.Where(m=>m.HasBody).Any(m=>Calls(m,"CreateNewCandidate")),"death and timed respawn never initialize a new character");
        var mining=Type("ActorMining");
        var miningIL=mining.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(!miningIL.Any(i=>i.Operand is MethodReference r&&r.DeclaringType.FullName=="Terraria.WorldGen"&&r.Name=="KillTile"),"tool executor never directly deletes a tile");
        Check(mining.Methods.Single(m=>m.Name=="TreeTarget").Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="IsATreeTrunk")
            &&mining.Methods.Single(m=>m.Name=="TreeTarget").Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="tileAxe"),"axe targets require both native axe and tree-trunk sets");
        var itemAdmission=Type("ActorItemUse").Methods.Single(m=>m.Name=="Supported").Body.Instructions
            .Select(i=>i.Operand).OfType<FieldReference>().Select(f=>f.Name).ToArray();
        Check(itemAdmission.Contains("shoot")&&itemAdmission.Contains("useAmmo")&&!itemAdmission.Contains("type"),
            "untargeted item admission checks native input requirements without an item ID whitelist");
        Check(types.Count(t=>t.BaseType?.FullName=="Terraria.ModLoader.Mod")==1&&Type("CompanionBridge").BaseType.FullName=="Terraria.ModLoader.Mod","one formal Mod entry; runtime is not a second mod");
        var save=Type("ProbePlayer").Methods.Single(m=>m.Name=="SaveData");
        Check(save.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldstr&&(string)i.Operand=="VirtualPlayerEngineProbe")
            && save.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="Actor")
            && save.Body.Instructions.Any(i=>i.OpCode.FlowControl==FlowControl.Cond_Branch),"probe persistence marker is guarded for formal human saves");
        Check(Type("VirtualPlayerEngineProbe").BaseType.FullName=="System.Object","promoted runtime hosted by formal Mod");
        var wire=types.Where(t=>t.FullName.StartsWith("CompanionBridge.ServerIpc.AiReadOnlyServer")).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(!wire.Any(i=>i.Operand is MemberReference r&&r.DeclaringType?.Namespace.StartsWith("Terraria")==true),"all TCP worker/state-machine IL contains zero Terraria access");
        var fence=Type("AiReplyFence");
        Check(fence.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).All(i=>i.Operand is not MemberReference r||r.DeclaringType?.Namespace.StartsWith("Terraria")!=true),"reply-turn worker fence contains zero Terraria access");
        Check(Calls(Type("AiReadOnlyProtocol").Methods.Single(m=>m.Name=="Handle"),"Publish"),"game-thread say passes final atomic reply-turn publication fence");
        Check(Calls(fence.Methods.Single(m=>m.Name=="Advance"),"Enter")&&Calls(fence.Methods.Single(m=>m.Name=="Publish"),"Enter"),"turn replacement and native publication use monitor critical sections");
        var adapter=Type("ServerAiSystem");var capture=adapter.Methods.Single(m=>m.Name=="Capture");var pump=adapter.Methods.Single(m=>m.Name=="Pump");
        Check(Calls(capture,"Require")&&Calls(capture,"AssertOwner"),"native snapshot reuses audited binding guard on owner thread");
        Check(!adapter.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference r&&r.Name=="get_LocalPlayer"),"server adapter has no LocalPlayer fallback");
        Check(Calls(pump,"get_IsMainThread")&&Calls(pump,"get_Gate")&&Calls(pump,"Tick"),"simulation pump enforces thread then Gate before dispatch");
        var select=adapter.Methods.Single(m=>m.Name=="Select");
        var say=adapter.Methods.Single(m=>m.Name=="Say");
        Check(Calls(say,"Require")&&Calls(say,"BroadcastChatMessageAs")
            &&say.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.DeclaringType.FullName is "Terraria.Player" or "Terraria.Entity"&&f.Name=="whoAmI"),"native reply checks AI ownership and broadcasts with the actual actor slot as author");
        Check(!say.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stsfld||i.OpCode==OpCodes.Stfld)
            &&!Calls(say,"SendChatMessageFromClient"),"AI reply neither mutates identity nor uses a fake human client send");
        var actions=Type("ServerAiNativeActions");
        var nativeInstructions=types.Where(t=>t.Name is "ServerAiNativeActions" or "ServerAiNavigation").SelectMany(t=>t.Methods)
            .Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(!nativeInstructions.Any(i=>i.Operand is MethodReference r&&r.Name is "get_LocalPlayer" or "PickTile" or "KillTile")
            &&!nativeInstructions.Any(i=>i.OpCode==OpCodes.Stsfld&&i.Operand is FieldReference f&&f.DeclaringType.FullName=="Terraria.Main"),"native adapter never spoofs global identity or directly mines/kills tiles");
        var placement=Type("ActorPlacement");
        var placementIL=placement.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(!placementIL.Any(i=>i.Operand is MethodReference r&&r.Name is "set_HasTile" or "PlaceTile" or "ConsumeItem"),
            "placement observes native effect/consumption without directly setting Tile or consuming items");
        var objectPlacement=Type("ActorObjectPlacement");
        Check(!objectPlacement.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference r&&r.Name is "set_HasTile" or "PlaceObject" or "PlaceTile" or "ConsumeItem"),"object executor only observes native placement, no Tile/inventory mutation");
        Check(Calls(objectPlacement.Methods.Single(m=>m.Name=="Definition"&&m.Parameters.Count==1),"GetTileData")&&Calls(objectPlacement.Methods.Single(m=>m.Name=="ReadFrames"),"get_TileFrameX"),"object placement reads selected native definition and verifies every frame");
        Check(Calls(Type("ActorItemUse").Methods.Single(m=>m.Name=="Update"),"AfterCheck")
            &&Calls(Type("ActorItemUse").Methods.Single(m=>m.Name=="SkipCursor"),"Dispatch"),"placement uses original ItemCheck owner stage and observes its consumption");
        Check(Calls(placement.Methods.Single(m=>m.Name=="Coordinate"),"get_IsMainThread")
            &&Calls(placement.Methods.Single(m=>m.Name=="Coordinate"),"Allowed"),"placement coordinate scope requires simulation thread and owned item execution");
        var nativeStart=actions.Methods.Single(m=>m.Name=="StartCore");
        Check(nativeStart.Body.Instructions.Count(i=>i.Operand is MethodReference r&&r.Name=="Start"&&r.DeclaringType.Name is "ServerAiNavigation" or "ActorMining" or "ActorItemUse")==4,"both navigation goals delegate to G4 boundary; use/mining retain G6/G7");
        Check(Calls(nativeStart,"StartPlacement"),"server placement dispatch uses existing item-use lifecycle");
        Check(Calls(nativeStart,"ReadLandingRegion")
            &&Type("ServerAiNavigation").Methods.Single(m=>m.Name=="BeforeMovement").Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="Region"),
            "validated immutable landing region reaches the existing native planner boundary");
        Check(Calls(Type("ServerAiNavigation").Methods.Single(m=>m.Name=="Update"),"Arrived")
            &&Calls(actions.Methods.Single(m=>m.Name=="Poll"),"Arrived"),"native arrival and final effect both verify the actual two-dimensional region");
        Check(!miningIL.Any(i=>i.Operand is MethodReference r&&r.Name=="add_GrabItems"),
            "sustained mining preserves ordinary native passive pickup");
        Check(Type("ServerAiNavigation").Fields.Any(f=>f.FieldType.Name=="NativeNavigation")
            &&Type("GameNavigation").Fields.Any(f=>f.FieldType.Name=="NativeNavigation"),"human and AI use separate instances of the original navigation implementation");
        Check(Calls(Type("ServerAiNavigation").Methods.Single(m=>m.Name=="Update"),"Invoke")
            &&Calls(Type("ActorControl").Methods.Single(m=>m.Name=="BeforeMovement"),"BeforeMovement"),"AI navigation wraps the original natural Update and G4 pre-movement boundary");
        var fixture=Type("ServerAiActionFixture");var enabled=fixture.Methods.Single(m=>m.Name=="get_Enabled");
        Check(enabled.Body.Instructions.Count(i=>i.Operand is MethodReference r&&r.Name=="ContainsKey")==2&&Calls(enabled,"Exists"),"diagnostic native frames require both test flags and isolated-world marker");
        Check(!fixture.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference r&&r.DeclaringType.FullName=="Terraria.Player"&&r.Name=="Update")
            &&!fixture.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.OpCode==OpCodes.Stsfld&&i.Operand is FieldReference f&&f.Name=="HasClients"),"diagnostic frames never manually update Player or pretend a real client exists");
        Check(Calls(select,"AssertOwner")&&select.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.DeclaringType.Name=="ActorItemUse"&&r.Name=="Select"),"server atomic selection delegates to G6 after owner assertion");
        Check(!select.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference r&&r.DeclaringType.FullName=="Terraria.Player"),"adapter does not reimplement native selection or mutate Player fields");
        var reads=Type("ServerAiQueries");var read=reads.Methods.Single(m=>m.Name=="Read");
        Check(read.Body.Instructions.Count(i=>i.Operand is MethodReference r&&r.DeclaringType.Name=="ServerAiQueries"&&r.Name=="Require")==2,"server query checks ownership before and after projection");
        Check(Calls(read,"Validate")&&Calls(read,"ReadInventory")&&Calls(read,"Read"),"queries reuse original argument validation, inventory projection and motion classifier");
        var readInstructions=reads.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(!readInstructions.Any(i=>i.Operand is MethodReference r&&(r.Name=="get_LocalPlayer"||r.Name=="Token"&&r.DeclaringType.Name=="AtomicInventory"))
            && !readInstructions.Any(i=>i.Operand is FieldReference f&&f.DeclaringType.FullName=="Terraria.Main"&&f.Name=="myPlayer"),"server reads neither LocalPlayer nor human-scoped inventory tokens");
        Check(!readInstructions.Any(i=>i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference f&&f.DeclaringType.FullName=="Terraria.Player"),"native state/inventory projections never write Player fields");
        Check(!Calls(adapter.Methods.Single(m=>m.Name=="OnWorldLoad"),"Start")&&!Calls(adapter.Methods.Single(m=>m.Name=="OnWorldLoad"),"Spawn"),"worker world-load callback never starts listener or actor");
        var started=adapter.Methods.Single(m=>m.Name=="Started");
        int CallOffset(string name)=>started.Body.Instructions.First(i=>i.Operand is MethodReference r&&r.Name==name).Offset;
        Check(CallOffset("Invoke")<CallOffset("Begin")&&CallOffset("Begin")<CallOffset("Pump"),"native server startup finishes before listener and actor publication");
        Check(Calls(adapter.Methods.Single(m=>m.Name=="OnWorldUnload"),"Stop")&&Calls(adapter.Methods.Single(m=>m.Name=="UnloadRuntime"),"Unload"),"world/mod unload stop IPC and delegate lifecycle teardown");
        foreach(string name in new[]{"ProbeCommand","ClientProbeCommand","ProbeTargetCommand","ProbeMouseMineCommand","ProbeTargetingSystem"})
            Check(Type(name).Methods.Any(m=>m.Name=="IsLoadingEnabled"&&m.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldstr&&(string)i.Operand=="VirtualPlayerEngineProbe")),"experimental content disabled for formal owner: "+name);
        var mod=Type("CompanionBridge");Check(Calls(mod.Methods.Single(m=>m.Name=="Load"),"LoadRuntime")&&Calls(mod.Methods.Single(m=>m.Name=="Load"),"RegisterKeybind"),"formal Mod retains independent server and human-client entry paths");
        var chat=Type("ChatBridgeSystem");
        Check(Calls(chat.Methods.Single(m=>m.Name=="Load"),"add_SendChatMessageFromClient")
            &&Calls(chat.Methods.Single(m=>m.Name=="Unload"),"remove_SendChatMessageFromClient"),"local outgoing chat capture has paired install/uninstall");
        Check(Calls(chat.Methods.Single(m=>m.Name=="Submit"),"Run")&&Calls(chat.Methods.Single(m=>m.Name=="Send"),"Suppress"),"local command capture shares existing own-reply suppression scope");
        var submitInstructions=Walk(chat).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(submitInstructions.Any(i=>i.Operand is MethodReference r&&r.Name=="get_IsConsumed")
            &&submitInstructions.Any(i=>i.Operand is GenericInstanceMethod r&&r.Name=="FromType"
                &&r.GenericArguments.Any(a=>a.Name=="SayChatCommand")),"ordinary local chat excludes consumed messages and non-Say commands");
        var chatInstructions=Walk(chat).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(chatInstructions.Any(i=>i.Operand is MethodReference r&&r.Name=="PublishLocalAiCommand")
            &&!chatInstructions.Any(i=>i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference f&&f.DeclaringType.FullName=="Terraria.Player"),"chat entry only publishes tasks and never writes player controls");
        var perception = Type("ServerAiQueries");
        var perceptionRead = perception.Methods.Single(m=>m.Name=="Read");
        Check(new[]{"ScanTiles","ScanEntities","InspectTile","Resolve"}.All(name=>Calls(perceptionRead,name)),"server perception reuses four AtomicQueries projections");
        Check(Calls(perceptionRead,"Read")&&Calls(perceptionRead,"ToUnixTimeMilliseconds"),"server perception reads terrain and stamps actual UTC time");
        var perceptionIL=Walk(perception).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(!perceptionIL.Any(i=>i.Operand is MethodReference r&&(r.Name is "get_LocalPlayer" or "CaptureCamera" || r.Name=="Query"&&r.DeclaringType.Name=="AtomicQueries"))
            && !perceptionIL.Any(i=>i.Operand is FieldReference f&&f.Name is "myPlayer" or "screenPosition"),"perception adapter has no human query/camera/global-player path");
        var scene=Type("ServerSceneReader");
        var sceneIL=Walk(scene).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(!sceneIL.Any(i=>i.Operand is MethodReference r&&r.Name is "PlaceTile" or "KillTile" or "set_HasTile" or "get_LocalPlayer" or "Move"),"scene observer never mutates terrain, simulates movement or falls back to human");
        Check(sceneIL.Any(i=>i.Operand is MethodReference r&&r.DeclaringType.Name=="NativeMotionWorld"&&r.Name=="SafeFlatLanding"),"scene landing candidates reuse native navigation flat landing checks");
        var craftingIL=Walk(Type("ActorCrafting")).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(!craftingIL.Any(i=>i.OpCode==OpCodes.Stsfld&&i.Operand is FieldReference f&&f.DeclaringType.Name=="Main"&&f.Name is "myPlayer" or "mouseItem"),"crafting never impersonates global player or mouse inventory");
        Check(craftingIL.Any(i=>i.Operand is MethodReference r&&r.DeclaringType.Name=="Player"&&r.Name=="GetItem")&&craftingIL.Any(i=>i.Operand is MethodReference r&&r.Name=="CollectedEnoughItemsToCraftRecipeNew"),"crafting reuses native material availability and inventory receipt");
        var inventoryIL=Walk(Type("ActorInventory")).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(new[]{"TransferWithLimit","StackItems","CanStack","Swap"}.All(name=>inventoryIL.Any(i=>i.Operand is MethodReference r&&r.Name==name)),"actor inventory uses native transfer/stack permission/swap");
        Check(!inventoryIL.Any(i=>i.Operand is MethodReference r&&r.Name is "get_LocalPlayer" or "FindRecipes")
            &&!inventoryIL.Any(i=>i.Operand is FieldReference f&&f.DeclaringType.Name=="Main"&&f.Name is "myPlayer" or "mouseItem"),"actor inventory never enters local UI or human path");
        Check(!inventoryIL.Any(i=>i.Operand is MethodReference r&&(r.DeclaringType.Name is "ItemIO" or "TagIO"||r.Name is "SaveData" or "NetSend")),"inventory tokens and evidence never serialize items or invoke Mod save/network hooks");
        var basic=Type("ActorInventory").Methods.Single(m=>m.Name=="BasicSlot");
        Check(!basic.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.DeclaringType.Namespace.StartsWith("Terraria")),"exception snapshot reads basic fields without Terraria or Mod method calls");
        var taskIL=Walk(Type("TaskSlot")).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(!taskIL.Any(i=>i.Operand is MethodReference r&&r.DeclaringType.Name is "AiReadOnlyServer" or "AiReadOnlyProtocol" or "AiAtomicActions"),"task runtime never re-enters top-level IPC/action dispatch");
        Check(Calls(Type("ChildExecutor").Methods.Single(m=>m.Name=="Start"),"StartCore"),"children start internal native contexts directly");
        Check(Calls(Type("TaskSlot").Methods.Single(m=>m.Name=="Tick"),"get_GameUpdate"),"task slot gates advancement on actual game update identity");
        Check(Calls(Type("NativeMineTargetWorld").Methods.Single(m=>m.Name=="CanMine"),"QueryReach")&&Calls(Type("NativeMineTargetWorld").Methods.Single(m=>m.Name=="FindApproach"),"QueryReach"),"actual stance and candidate intervals use the native mining range query");
        var queryReach=Type("ActorMining").Methods.Single(m=>m.Name=="QueryReach");
        Check(Calls(queryReach,"Capture")&&queryReach.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="inRange"),"reach projection keeps actor binding and audited native delegate");
        var taskResult=Type("ServerAiNativeActions").Methods.Single(m=>m.Name=="TaskResult");
        Check(Calls(taskResult,"Owned")&&Calls(taskResult,"Released"),"root result rechecks actual actor binding and input release");
        var taskTick=Type("MineTargetTask").Methods.Single(m=>m.Name=="Tick");
        Check(Type("AiAtomicActions").Methods.Single(m=>m.Name=="get_Capabilities").Body.Instructions.Any(
            i=>i.OpCode==OpCodes.Ldstr&&(string)i.Operand=="server_ai_program_tasks_v1"),"new task safety/lifecycle semantics have an explicit capability fence");
        var hazard=Type("NativeNavigationWorld").Methods.Single(m=>m.Name=="CheckHazards");
        Check(Calls(taskTick,"InterruptionReason")&&Calls(taskTick,"Stop"),"root task checks hazards in game-tick execution and uses normal child cleanup");
        Check(Calls(hazard,"Require")&&hazard.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="npc")
            &&hazard.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="statLife")
            &&hazard.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="lavaWet"),"hazard reads bound actor health/lava and current native NPCs");
        Check(!hazard.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference f&&f.DeclaringType.Name=="Player"),"hazard detection never rewrites Player motion or controls");
        var movement=Type("NativeMovementReader").Methods.Single(m=>m.Name=="Capture");
        var nativeCalls=movement.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>().Where(r=>r.DeclaringType.Namespace.StartsWith("Terraria")).ToArray();
        var allowed=new[]{"get_OrderedJumps","GetJumpState","get_FullName","get_Enabled","get_Available","get_Active","get_Type","get_MountCount","GetMount"};
        Check(nativeCalls.All(r=>allowed.Contains(r.Name)),"movement discovery calls only audited registry/state getters, never extension hooks");
        Check(!Type("NativeNavigation").Methods.Where(m=>m.HasBody).Any(m=>Calls(m,"AnyExtraJumpUsable")),"navigation admission never calls hook-bearing AnyExtraJumpUsable");
        Check(Calls(Type("NativeNavigation").Methods.Single(m=>m.Name=="Before"),"Capture"),"movement snapshot sampled by existing navigation Before");
        var boundary=Type("ProbePlayer").Methods.Single(m=>m.Name=="PostUpdateRunSpeeds");
        Check(boundary.Body.Instructions.Count(i=>i.Operand is MethodReference r&&r.Name=="BeforeMovement")==1&&!Calls(boundary,"Update"),"one native run-speeds navigation boundary, no extra Player.Update");
        Check(!movement.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference f&&f.DeclaringType.Namespace.StartsWith("Terraria")),"movement collector never mutates native game state");
        Check(types.Where(t=>t.Namespace=="CompanionBridge.Motion").SelectMany(t=>t.Methods).Where(m=>m.HasBody)
            .SelectMany(m=>m.Body.Instructions).All(i=>i.Operand is not MethodReference r||r.DeclaringType.Namespace!="Terraria.ModLoader"
                ||!new[]{"ProcessJumps","PerformJump","CanStart","CanStartExtraJump","GetDurationMultiplier","OnStarted"}.Contains(r.Name)),
            "prediction/execution never probes or directly invokes native ExtraJump hooks");
        Check(Calls(Type("LocalMotionController").Methods.Single(m=>m.Name=="After"),"MatchesResource"),"native execution verifies actual activation and counters");
        Console.WriteLine($"AI formal package: {checks} IL checks passed; no graphical game launched.");
    }
}
