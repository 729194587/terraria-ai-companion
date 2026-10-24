using System;
using System.Collections.Generic;
using System.IO;

// Transport substitutes for compiling/testing the production Mod packet adapter.
// They never claim a socket connection to Terraria or native game execution.
namespace Terraria.Localization { public class NetworkText { } }
namespace Terraria.ModLoader
{
    public class ModSystem
    {
        public virtual void Load() { }
        public virtual void Unload() { }
        public virtual void OnWorldLoad() { }
        public virtual void OnWorldUnload() { }
        public virtual void PostUpdateEverything() { }
    }
    public static class ModContent
    {
        public static T GetInstance<T>() => (T)(object)new global::CompanionBridge.CompanionBridge();
    }
    public sealed class ModPacket : BinaryWriter
    {
        public static readonly List<(int Target,byte[] Bytes)> Sent=new();
        public ModPacket() : base(new MemoryStream()) { }
        public void Send(int target=-1) { Sent.Add((target,((MemoryStream)BaseStream).ToArray())); }
    }
}
namespace Terraria
{
    public static class On_NetMessage
    {
        public delegate void orig_SendData(int msg,int remote,int ignore,Terraria.Localization.NetworkText text,int number,float n2,float n3,float n4,int n5,int n6,int n7);
        public delegate void hook_SendData(orig_SendData orig,int msg,int remote,int ignore,Terraria.Localization.NetworkText text,int number,float n2,float n3,float n4,int n5,int n6,int n7);
        public static event hook_SendData SendData;
        public static void Invoke(orig_SendData orig,int msg,int remote,int ignore,Terraria.Localization.NetworkText text,int number,float n2,float n3,float n4,int n5,int n6,int n7)
        {
            if(SendData==null) orig(msg,remote,ignore,text,number,n2,n3,n4,n5,n6,n7);
            else SendData(orig,msg,remote,ignore,text,number,n2,n3,n4,n5,n6,n7);
        }
    }
}
