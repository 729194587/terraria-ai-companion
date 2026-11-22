using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ModLoader;
using Microsoft.Xna.Framework;

namespace CompanionBridge.ServerIpc;

// Registration takes only content names and primitive values. No foreign code runs in the combat loop.
internal static class BossExtensions
{
    private static readonly Dictionary<int,bool> roots=new();
    private static readonly Dictionary<(int root,int part),int> links=new();
    private static readonly Dictionary<int,float?> gravity=new();
    private static bool sealedRegistry;
    internal static object Register(object[] args)
    {
        if(args.Length<2 || args[0] is not string op || op!="BossExtension")return "unknown_operation";
        if(args[1] is not int version || version!=1)return "unsupported_version";
        if(sealedRegistry)return "registration_closed";
        if(args is [_,_,"root",string root] && ModContent.TryFind<ModNPC>(root,out var boss))
        {roots[boss.Type]=true;return "accepted";}
        if(args is [_,_,"member",string parent,string part,int index] && index is >=0 and <4
            && ModContent.TryFind<ModNPC>(parent,out var p) && ModContent.TryFind<ModNPC>(part,out var n))
        {
            var key=(p.Type,n.Type);
            if(links.TryGetValue(key,out var prior) && prior!=index){links[key]=-1;return "conflict";}
            links[key]=index;return "accepted";
        }
        if(args is [_,_,"gravity",string shot,float acceleration] && float.IsFinite(acceleration)
            && Math.Abs(acceleration)<=1 && ModContent.TryFind<ModProjectile>(shot,out var projectile))
        {
            if(gravity.TryGetValue(projectile.Type,out var prior) && prior!=acceleration)
            {gravity[projectile.Type]=null;return "conflict";}
            gravity[projectile.Type]=acceleration;return "accepted";
        }
        return "invalid_descriptor";
    }
    internal static bool Root(int type){sealedRegistry=true;return roots.ContainsKey(type);}
    internal static bool Link(NPC root,NPC part)=>links.TryGetValue((root.type,part.type),out int i)&&i>=0&&part.ai[i]==root.whoAmI;
    internal static float Gravity(int type)=>gravity.GetValueOrDefault(type)??0;
    internal static bool Conflicted(int type)=>gravity.TryGetValue(type,out var value)&&!value.HasValue;
    internal static int Conflicts=>links.Values.Count(i=>i<0)+gravity.Values.Count(v=>!v.HasValue);
    internal static void Reset(){sealedRegistry=false;roots.Clear();links.Clear();gravity.Clear();}
}
