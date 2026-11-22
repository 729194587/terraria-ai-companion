using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.DataStructures;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Copied-world diagnostic only. Initial state injection never participates in action execution.
internal static class ServerAiDirectInputFixture
{
    private static readonly List<object> frames=new();
    private static int settle;
    private static string scene;
    private static object ready;
    private static bool Enabled=>ServerAiActionFixture.Enabled&&Program.LaunchParameters.ContainsKey("-companiondirectinputtest");
    internal static void Input(Player p)
    {
        if(!Enabled)return;
        frames.Add(new{tick=Main.GameUpdateCount,left=p.controlLeft,right=p.controlRight,up=p.controlUp,down=p.controlDown,
            jump=p.controlJump,releaseJump=p.releaseJump,x=p.position.X,y=p.position.Y,vx=p.velocity.X,vy=p.velocity.Y});
        try{File.WriteAllText(Path.Combine(Main.SavePath,"direct-input-frames.json"),JsonSerializer.Serialize(frames));}
        catch(IOException){ /* A diagnostic reader may briefly hold the file. Next update persists the trace. */ }
    }
    internal static void Result(object result)
    {
        if(!Enabled)return;
        try{File.WriteAllText(Path.Combine(Main.SavePath,"direct-input-terminal.json"),JsonSerializer.Serialize(result));}
        catch(IOException){ }
    }
    internal static void Tick(Player p)
    {
        if(!Enabled)return;
        foreach(var npc in Main.npc)if(npc.active&&!npc.townNPC)npc.active=false;
        string eventPath=Path.Combine(Main.SavePath,"direct-input-event.json");
        if(File.Exists(eventPath))
        {
            string text;
            try{text=File.ReadAllText(eventPath);File.Delete(eventPath);}catch(IOException){return;}
            using var command=JsonDocument.Parse(text);
            string cause=command.RootElement.GetProperty("cause").GetString();
            if(cause=="retire")ActorControl.Retire(p);
            else if(cause=="death")NativeSurvival.Run(p,()=>p.KillMe(PlayerDeathReason.ByCustomReason(Terraria.Localization.NetworkText.FromLiteral("Direct input diagnostic death")),9999,0));
            else throw new ArgumentException("Unknown direct-input event.");
        }
        if(settle>0){if(--settle==0)File.WriteAllText(Path.Combine(Main.SavePath,"direct-input-ready.json"),JsonSerializer.Serialize(ready));return;}
        string request=Path.Combine(Main.SavePath,"direct-input-request.json");
        if(!File.Exists(request))return;
        if(ActorControl.Running||ActorItemUse.Running||ActorMining.Running)throw new InvalidOperationException("Direct-input fixture cannot replace active action.");
        string requestText;
        try{requestText=File.ReadAllText(request);File.Delete(request);}catch(IOException){return;}
        using var doc=JsonDocument.Parse(requestText);scene=doc.RootElement.GetProperty("scene").GetString();
        if(scene.StartsWith("pylon-") && Program.LaunchParameters.ContainsKey("-companionpylontest"))
        {
            ready=PylonDiagnostic.Setup(scene,p);
            settle=3;
            return;
        }
        if(scene=="bed-readback")
        {
            var id=CharacterStore.Save();
            var files=new CharacterFiles(Main.SavePath);
            var loaded=CharacterIo.Read(files.Open(id).Path);
            loaded.FindSpawn();
            File.WriteAllText(Path.Combine(Main.SavePath,"direct-input-ready.json"),
                JsonSerializer.Serialize(new{x=loaded.SpawnX,y=loaded.SpawnY}));
            return;
        }
        if(scene=="regroup-native")
        {
            // Detached observer, never inserted into a live slot or returned by teammate discovery.
            var other=new Player { team=3, position=p.position+new Microsoft.Xna.Framework.Vector2(128,0) };
            var frame=new AiReadOnlyProtocol.Identity(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,p.whoAmI,null);
            var before=p.position;
            var missing=ServerRegroup.UseWormhole(frame,other,"fixture");
            p.inventory[20].SetDefaults(ItemID.WormholePotion);
            p.inventory[20].stack=2;
            other.team=4;
            var different=ServerRegroup.UseWormhole(frame,other,"fixture");
            other.team=3;other.dead=true;
            var dead=ServerRegroup.UseWormhole(frame,other,"fixture");
            other.dead=false;
            var success=ServerRegroup.UseWormhole(frame,other,"fixture");
            File.WriteAllText(Path.Combine(Main.SavePath,"direct-input-ready.json"),JsonSerializer.Serialize(new {
                missing=missing.Reason,different=different.Reason,dead=dead.Reason,success=success.Reason,
                deltaX=p.position.X-before.X, remaining=p.inventory[20].stack, success.Evidence }));
            return;
        }
        if(scene is not ("flat" or "wall" or "platform" or "edge" or "air" or "profile_refused" or "door" or "blocked_door" or "lever" or "hammer" or "bed" or "bed-reversed" or "bed-invalid" or "open_items"))throw new ArgumentException("Unknown direct-input fixture scene.");
        int left=Main.spawnTileX,floor=Main.spawnTileY+8;
        for(int tx=left-36;tx<=left+36;tx++)for(int ty=floor-24;ty<=floor+18;ty++)
        {
            var tile=Main.tile[tx,ty];tile.ClearEverything();
            int surface=scene is "platform" or "edge"?floor+12:floor;
            if(ty>=surface||scene=="wall"&&tx>=left+2&&ty>=floor-12)
            {tile.HasTile=true;tile.TileType=TileID.Dirt;}
            if((scene=="platform"||scene=="edge"&&tx<=left+2)&&ty==floor)
            {tile.HasTile=true;tile.TileType=TileID.Platforms;tile.TileFrameY=0;tile.TileFrameX=0;}
        }
        if(scene.StartsWith("bed"))
        {
            if(scene!="bed-invalid")
                for(int x=left-5;x<=left+8;x++)
                for(int y=floor-7;y<floor;y++)
                {
                    var tile=Main.tile[x,y];tile.WallType=WallID.Wood;
                    if(x==left-5 || x==left+8 || y==floor-7){tile.HasTile=true;tile.TileType=TileID.WoodBlock;}
                }
            if(!TileObject.CanPlace(left+2,floor-1,TileID.Beds,0,scene=="bed-reversed"?-1:1,out var bed) || !TileObject.Place(bed))
                throw new InvalidOperationException("Could not place diagnostic bed");
        }
        if(scene=="hammer")
        {
            p.inventory[3].SetDefaults(ItemID.WoodenHammer);
            p.inventory[4].SetDefaults(ItemID.WoodenSword);
            var exposed=Main.tile[left+2,floor-1];exposed.WallType=WallID.Wood;
            var covered=Main.tile[left+3,floor];covered.WallType=WallID.Wood;
            var platform=Main.tile[left-2,floor];platform.TileType=TileID.Platforms;
        }
        if(scene=="lever")
        {
            int y=floor-1;
            var backing=Main.tile[left-2,y];
            backing.WallType=WallID.Wood;
            if(!WorldGen.PlaceTile(left-2,y,TileID.Switches,forced:true))
                throw new InvalidOperationException("Could not place diagnostic switch");
            var lintel=Main.tile[left-4,y-3];lintel.HasTile=true;lintel.TileType=TileID.Dirt;
            if(!TileObject.CanPlace(left-4,y,TileID.ClosedDoor,0,1,out var door)||!TileObject.Place(door))
                throw new InvalidOperationException("Could not place diagnostic door");
            foreach(int x in new[]{left+3,left+12})
                if(!WorldGen.PlaceTile(x,y,TileID.Lever,forced:true))
                    throw new InvalidOperationException("Could not place diagnostic lever");
            if(!WorldGen.PlaceTile(left+7,y,TileID.Torches,forced:true))
                throw new InvalidOperationException("Could not place diagnostic torch");
            for(int x=left+3;x<=left+7;x++){var tile=Main.tile[x,y];tile.RedWire=true;}
        }
        if(scene=="open_items")
        {
            p.inventory[20].SetDefaults(ItemID.EyeOfCthulhuBossBag);p.inventory[20].stack=2;
            p.inventory[21].SetDefaults(ItemID.WoodenCrate);p.inventory[22].SetDefaults(ItemID.StoneBlock);
        }
        if(scene is "door" or "blocked_door")
        {
            int x=left+3;
            var lintel=Main.tile[x,floor-4];lintel.HasTile=true;lintel.TileType=TileID.Dirt;
            if(!TileObject.CanPlace(x,floor-1,TileID.ClosedDoor,0,1,out var data)||!TileObject.Place(data))
                throw new InvalidOperationException("Could not place diagnostic door");
            if(scene=="blocked_door")
                foreach(int side in new[]{-1,1})
                {
                    var block=Main.tile[x+side,floor-3];block.HasTile=true;block.TileType=TileID.Dirt;
                }
        }
        p.position=new(left*16,floor*16-p.height-(scene=="air"?300:0));p.velocity=new(0,0);
        p.jump=0;p.releaseJump=true;p.statLife=100;p.dead=false;
        if(scene=="profile_refused")p.AddBuff(BuffID.WellFed,3600); // Diagnostic profile refusal; raw inputs still use native physics.
        p.controlLeft=p.controlRight=p.controlJump=p.controlDown=p.controlUp=false;
        frames.Clear();
        ready=new{scene,startX=p.position.X,startY=p.position.Y,floorY=floor*16};
        settle=scene=="air"?1:20;
    }
}
