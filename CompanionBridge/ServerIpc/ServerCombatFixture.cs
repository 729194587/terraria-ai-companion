using System;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Only the isolated, copied-world harness can seed a target. All attacks run in natural frames.
internal static class ServerCombatFixture
{
    internal static bool Enabled => ServerAiActionFixture.Enabled && Program.LaunchParameters.ContainsKey("-companioncombattest");
    private static NPC target;
    internal static bool Keeps(NPC npc) => Enabled && ReferenceEquals(npc,target);

    internal static void Tick(Player p)
    {
        if(!Enabled)return;
        var request=Path.Combine(Main.SavePath,"combat-request");
        if(!File.Exists(request))
        {
            if(target?.active==true)target.timeLeft=750;
            return;
        }
        string mode=File.ReadAllText(request).Trim();
        File.Delete(request);
        if(mode=="agent"&&p.HeldItem.type==ItemID.WoodenSword)
        {
            p.HeldItem.SetDefaults(ItemID.PlatinumBroadsword);
            p.statLife=p.statLifeMax2;
        }
        if(target?.active==true)target.active=false;
        int floor=Main.spawnTileY+8;
        // Keep the arena floor under the native knockback travel.
        int center=Main.spawnTileX;
        for(int tx=center-75;tx<=center+75;tx++)
        for(int ty=floor-12;ty<=floor+2;ty++)
        {
            var tile=Main.tile[tx,ty];
            tile.ClearEverything();
            if(ty>=floor){tile.HasTile=true;tile.TileType=TileID.Dirt;}
        }
        int x=(int)p.Center.X+(mode=="agent"?-180:-46);
        int slot=NPC.NewNPC(p.GetSource_Misc("isolated_combat_fixture"),x,floor*16,NPCID.GreenSlime,
            Target:mode=="agent"?255:p.whoAmI);
        target=Main.npc[slot];
        // Keep the seeded target inside the arena without editing its life or hitbox.
        // With AI disabled the NPC has no vanilla AI friction to slow knockback.
        target.aiStyle=-1;
        target.knockBackResist=0;
        target.Bottom=new Vector2(x,floor*16);
        target.target=mode=="agent"?255:p.whoAmI;
        var identity=new AiReadOnlyProtocol.Identity(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,p.whoAmI,null);
        File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(new{
            entityRef=NativeCombatWorld.Reference(identity,target),life=target.life,slot,mode}));
    }
}
