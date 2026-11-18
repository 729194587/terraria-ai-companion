using System;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Microsoft.Xna.Framework;

namespace BossStrategySample;

public sealed class Registration : ModSystem
{
    public override void PostSetupContent()
    {
        if(!ModLoader.TryGetMod("CompanionBridge",out var core))return;
        void Check(string expected,params object[] args)
        {
            if(!Equals(expected,core.Call(args)))throw new InvalidOperationException("Descriptor check failed: "+args[2]);
        }
        Check("unsupported_version","BossExtension",2,"root","BossStrategySample/Root");
        Check("accepted","BossExtension",1,"root","BossStrategySample/Root");
        Check("accepted","BossExtension",1,"member","BossStrategySample/Root","BossStrategySample/Linked",2);
        Check("accepted","BossExtension",1,"gravity","BossStrategySample/Arc",.2f);
        Check("accepted","BossExtension",1,"gravity","BossStrategySample/Conflicted",.1f);
        Check("conflict","BossExtension",1,"gravity","BossStrategySample/Conflicted",.3f);
    }
}
public class Root : ModNPC
{
    public override string Texture=>"Terraria/Images/NPC_4";
    public override void SetDefaults()
    {
        NPC.width=50;NPC.height=50;NPC.lifeMax=3500;NPC.damage=12;NPC.boss=true;
        NPC.noGravity=NPC.noTileCollide=true;NPC.knockBackResist=0;NPC.aiStyle=-1;
    }
    public override void AI()
    {
        NPC.timeLeft=600;
        if(Main.netMode==NetmodeID.MultiplayerClient)return;
        if(NPC.ai[0]++==0)
        {
            var part=NPC.NewNPC(NPC.GetSource_FromAI(),(int)NPC.Center.X+40,(int)NPC.Center.Y,ModContent.NPCType<Segment>());
            Main.npc[part].realLife=NPC.whoAmI;
            NPC.NewNPC(NPC.GetSource_FromAI(),(int)NPC.Center.X-40,(int)NPC.Center.Y,ModContent.NPCType<Linked>(),ai2:NPC.whoAmI);
        }
        if((int)NPC.ai[0]%30==3)
            Projectile.NewProjectile(NPC.GetSource_FromAI(),NPC.Center,new Vector2(4,-3),(int)NPC.ai[0]%60==3?ModContent.ProjectileType<Arc>():ModContent.ProjectileType<Conflicted>(),8,0);
    }
}
public class Segment : ModNPC
{
    public override string Texture=>"Terraria/Images/NPC_1";
    public override void SetDefaults(){NPC.width=22;NPC.height=22;NPC.lifeMax=3500;NPC.damage=5;NPC.aiStyle=-1;NPC.noGravity=true;NPC.knockBackResist=0;}
}
public class Linked : Segment {}
public class Arc : ModProjectile
{
    public override string Texture=>"Terraria/Images/Projectile_1";
    public override void SetDefaults(){Projectile.width=8;Projectile.height=8;Projectile.hostile=true;Projectile.timeLeft=100;}
    public override void AI(){Projectile.velocity.Y+=.2f;}
}
public class Conflicted : Arc {}
