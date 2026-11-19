// Deterministic engine boundary for the ACTUAL ServerSceneReader. Not native acceptance.
using System;
using System.Collections.Generic;
using CompanionBridge;
using CompanionBridge.Motion;

internal static class SceneReaderFixture
{
    internal static SceneCell[,] Cells;
    internal static Func<int,int,SceneCell> RegionalCell;
    internal static int Reads,Anchors,Worlds,LandingChecks,Definitions,TileMaterials,ObjectMaterials,EntityChecks;
    internal static void ResetCounts(){Reads=Anchors=Worlds=LandingChecks=Definitions=TileMaterials=ObjectMaterials=EntityChecks=0;}
    internal static char Nav(int x,int y)=>RegionalCell!=null?RegionalCell(x,y).Navigation:x<0||x>=45||y<2||y>=31?'?':Cells[x,y-2].Navigation;
}
namespace CompanionBridge{internal sealed record StateVector(float X,float Y);}
namespace Terraria
{
    public record struct Vector(float X,float Y);
    public class Entity
    {
        public Vector position,velocity;
        public int width=16,height=16,whoAmI;
        public bool active=true;
        public Vector Center=>new(position.X+width/2f,position.Y+height/2f);
    }
    public class Player:Entity
    {
        public bool dead,ghost;public int selectedItem;
        public Vector Bottom=>new(position.X+width/2f,position.Y+height);
        public Item HeldItem=new(){type=36,stack=8,createTile=18};
    }
    public class NPC:Entity
    {
        public bool friendly,townNPC;public int damage=20,type=1,life=50;
        public ModLoader.ModType ModNPC;
        public bool CanBeChasedBy(){SceneReaderFixture.EntityChecks++;return !friendly;}
        public T GetGlobalNPC<T>() where T:new()=>new();
    }
    public class Item:Entity
    {public int type=1,stack=3,createTile=-1;public bool IsAir=>stack<=0;public ModLoader.ModType ModItem;}
    public class Projectile:Entity{public bool hostile=true;public int damage=30,type=1,extraUpdates;}
    public static class Main
    {
        public static int[,] tile=new int[256,256];
        public static bool[] tileCut=new bool[1000];
        public static ulong GameUpdateCount=42;
        public static Player[] player;
        public static NPC[] npc;
        public static Item[] item;
        public static Projectile[] projectile;
    }
}
namespace Terraria.ModLoader
{
    public class ModType{public string FullName;}
    public static class TileLoader{public static ModType GetTile(int id)=>null;}
}
namespace Terraria.ID
{
    public class Search{public string GetName(int type)=>"Type"+type;}
    public static class TileID
    {
        public const int Torches=4,Copper=7,Tin=166,Iron=6,Lead=167,Silver=9,Tungsten=168,Gold=8,Platinum=169,
            Demonite=22,Crimtane=204,Meteorite=37,Hellstone=58,Cobalt=107,Palladium=221,Mythril=108,Orichalcum=222,
            Adamantite=111,Titanium=223,Chlorophyte=211,Containers=21,Containers2=467,Dressers=88,OpenDoor=11,ClosedDoor=10,
            Chairs=15,WorkBenches=18,HoneyBlock=229,Cobweb=51,Asphalt=234,SlimeBlock=196,FrozenSlimeBlock=197,Count=693;
        public static Search Search=new();
        public static class Sets{public static bool[] IceSkateSlippery=new bool[1000],IsATreeTrunk=new bool[1000],BreakableWhenPlacing=new bool[1000];}
    }
    public static class ItemID{public static Search Search=new();}
    public static class NPCID
    {public static Search Search=new();public static class Sets{public static bool[] ActsLikeTownNPC=new bool[1000],CountsAsCritter=new bool[1000],ProjectileNPC=new bool[1000];}}
}
namespace Terraria.ObjectData
{
    public sealed class TileObjectData
    {
        public int Width=2,Height=1;
        public (int X,int Y) Origin;
        public static TileObjectData GetTileData(int tile)=>new();
        public static (int X,int Y) TopLeft(int x,int y)
        {
            SceneReaderFixture.Anchors++;
            var c=SceneReaderFixture.Cells[x,y-2].Facts;
            return (x-c.FrameX/18%2,y-c.FrameY/18%3);
        }
    }
}
namespace CompanionBridge.ServerIpc
{
    internal static class AiReadOnlyProtocol{internal sealed record Identity(object Actor,string World,long Generation,int Slot);}
    internal static class SceneMineTargets
    {internal static object Observe(AiReadOnlyProtocol.Identity identity,int x,int y)=>new{x,y,world=identity.World,generation=identity.Generation,slot=identity.Slot};}
}
namespace CompanionBridge.Common.Systems
{
    public class AtomicNpcIdentity{public string Token="fixture-token";}
    internal static class ObservationReader
    {
        internal static TileCell ReadCell(int x,int y,Dictionary<(int,int),object> anchors)
        {SceneReaderFixture.Reads++;return SceneReaderFixture.RegionalCell!=null?SceneReaderFixture.RegionalCell(x,y).Facts:SceneReaderFixture.Cells[x,y-2].Facts;}
    }
    internal static class TerrainReader
    {
        internal static char ReadCell(int x,int y)=>SceneReaderFixture.Nav(x,y);
        internal static TerrainSnapshot Read(Terraria.Player p)=>new(0,2,16,Array.Empty<string>(),20,42,true,true);
    }
}
namespace CompanionBridge.Motion
{
    internal static class GameNavigation{internal static bool Ordinary(Terraria.Player p)=>p.width==20&&p.height==42;}
    internal sealed class NativeMotionWorld
    {
        internal NativeMotionWorld(PhysicsState state){SceneReaderFixture.Worlds++;}
        internal bool SafeFlatLanding(PhysicsState state)
        {SceneReaderFixture.LandingChecks++;return MotionSupport.SafeFlatLanding(state,SceneReaderFixture.Nav);}
    }
}
namespace VirtualPlayerEngineProbe
{
    internal static class ActorPlacement
    {internal static bool Supported(Terraria.Item item){SceneReaderFixture.TileMaterials++;return false;}}
    internal static class ActorObjectPlacement
    {
        internal sealed record Shape(int Width,int Height,int OriginX,int OriginY,int SupportStart,int SupportCount);
        internal static Shape Definition(){SceneReaderFixture.Definitions++;return new(2,1,0,0,0,2);}
        internal static bool Supported(Terraria.Item item){SceneReaderFixture.ObjectMaterials++;return item.type==36;}
    }
}
