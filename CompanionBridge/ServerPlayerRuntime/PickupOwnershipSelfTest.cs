using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using MonoMod.Utils;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;

namespace VirtualPlayerEngineProbe;

// No client connection is fabricated. An existing inactive Player is temporarily
// used as an in-memory ownership candidate ONLY in a fresh diagnostic profile.
internal static class PickupOwnershipSelfTest
{
    private static bool ran;
    private static int watched=-1,items,owners;
    private static void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);}
    private static void FixtureHuman(Player p){p.active=true;}
    private static void Observe(On_NetMessage.orig_SendData orig,int msg,int remote,int ignore,NetworkText text,int number,float a,float b,float c,int d,int e,int f)
    {
        if(number==watched){if(msg==MessageID.SyncItem)items++;if(msg==MessageID.ItemOwner)owners++;}
        orig(msg,remote,ignore,text,number,a,b,c,d,e,f);
    }
    internal static void RunIfRequested()
    {
        if(ran||!Environment.GetCommandLineArgs().Contains("-pickupownershipselftest"))return;
        ran=true;
        lock(ProbeSession.Reservation.Gate)Run();
    }
    private static void Run()
    {
        string report=Path.Combine(Main.SavePath,"pickup-ownership-native-result.json");
        var human=Main.player[0];bool active=human.active,dead=human.dead;Vector2 position=human.position;
        try
        {
            Main.gameMenu=false;ProbeSession.Spawn();var p=ProbeSession.Actor;
            Check(!active&&!human.dead,"Diagnostic requires an unused native Player slot, never a human connection.");
            FixtureHuman(human);human.dead=false;human.position=p.position+new Vector2(800,0);
            watched=Item.NewItem(new EntitySource_Misc("pickup-ownership-regression"),human.Hitbox,ItemID.StoneBlock,7);
            var item=Main.item[watched];
            void Reset(int owner){FixtureHuman(human);item.playerIndexTheItemIsReservedFor=owner;item.keepTime=0;item.noGrabDelay=0;items=owners=0;}
            // DMD from the pinned DLL original body: neither injected IL guard is present.
            using var mainDmd=new DynamicMethodDefinition(typeof(Main).GetMethod("UpdateServer",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static));
            var baseline=(Action)mainDmd.Generate().CreateDelegate(typeof(Action));
            using var ownerDmd=new DynamicMethodDefinition(typeof(Item).GetMethod("FindOwner",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance));
            var baselineOwner=(Action<Item,int>)ownerDmd.Generate().CreateDelegate(typeof(Action<Item,int>));
            var update=typeof(Main).GetMethod("UpdateServer",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);
            // Native CheckSection deactivates this unconnected diagnostic candidate
            // after the item loop. Restore only the fixture before each observation;
            // do not fake a RemoteClient or skip the real native server method.
            void Update(){FixtureHuman(human);update.Invoke(null,null);}
            On_NetMessage.SendData+=Observe;
            Reset(p.whoAmI);baseline();
            Check(item.playerIndexTheItemIsReservedFor==p.whoAmI&&items==0&&owners==0,"Unpatched server did not reproduce stale AI ownership.");
            Reset(p.whoAmI);baselineOwner(item,watched);
            Check(item.playerIndexTheItemIsReservedFor==0&&items==0&&owners==0,$"Unpatched native handoff result: owner={item.playerIndexTheItemIsReservedFor}, packets={items}/{owners}, sWidth={NPC.sWidth}, candidate={human.active}/{human.dead}/{human.ghost}, position={human.position}/{item.position}, range={human.GetItemGrabRange(item)}, myPlayer={Main.myPlayer}, AIactive={p.active}.");
            Reset(p.whoAmI);Update();
            Check(item.playerIndexTheItemIsReservedFor==0&&items==1&&owners==1,"Fixed AI-to-human handoff did not send exactly native 21/22.");
            Check(item.stack==7&&item.active,"Handoff fabricated a pickup or deleted the item.");
            items=owners=0;Update();
            Check(item.playerIndexTheItemIsReservedFor==0&&items==0&&owners==0,"Human-owned item no longer follows native client authority.");
            human.position=p.position+new Vector2(1600,0);item.position=p.position;
            Reset(p.whoAmI);Update();Update();
            Check(item.playerIndexTheItemIsReservedFor==p.whoAmI&&items==0&&owners==0,"Unchanged AI ownership emitted duplicate handoffs.");
            item.keepTime=30;Update();
            Check(item.keepTime==30&&item.playerIndexTheItemIsReservedFor==p.whoAmI&&owners==0,"Native keepTime was bypassed.");
            item.keepTime=0;
            int before=p.inventory.Where(i=>i.type==ItemID.StoneBlock).Sum(i=>i.stack);
            var grab=typeof(Player).GetMethod("GrabItems",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
            NativeSurvival.Run(p,()=>grab.Invoke(p,new object[]{p.whoAmI}));
            Check(p.inventory.Where(i=>i.type==ItemID.StoneBlock).Sum(i=>i.stack)==before+7&&Main.item[watched].IsAir,"AI native pickup regressed.");
            Check(Main.myPlayer==255&&!Netplay.Clients[0].IsConnected(),"Diagnostic fabricated a local identity/client.");
            human.active=active;human.dead=dead;human.position=position;
            ProbeSession.Finish("pickup ownership native selftest",true);
            File.WriteAllText(report,JsonSerializer.Serialize(new{result="passed",baselineStaleAiOwner=true,baselineMissingHandoffPackets=true,
                fixedHumanOwner=0,syncItemPackets=1,itemOwnerPackets=1,humanPathUnchanged=true,noDuplicatePackets=true,
                nativeKeepTime=true,aiPickup=true,hostAndPlay="not executed",remoteMultiplayer="not executed"}));
        }
        catch(Exception e)
        {
            File.WriteAllText(report,JsonSerializer.Serialize(new{result="failed",error=e.ToString()}));
            VirtualPlayerEngineProbe.Instance?.Logger.Error("Pickup ownership native regression failed",e);
        }
        finally
        {
            On_NetMessage.SendData-=Observe;human.active=active;human.dead=dead;human.position=position;watched=-1;Netplay.Disconnect=true;
        }
    }
}
