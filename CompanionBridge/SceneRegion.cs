using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompanionBridge;

internal sealed record SceneRegionRow(string Encoding,object Data,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? IndexDigits=null);
internal sealed record SceneRegionGrid<T>(int EncodingVersion,T[] Palette,SceneRegionRow[] Rows);
internal readonly record struct SceneKnowledge(string Tile,string Collision,bool NavigationKnown);
internal readonly record struct SceneRegionObjectCell(string Kind,string ContentKey);

// Lossless per-Tile encoding: no majority vote can erase a one-Tile hazard,
// unknown cell, slope, half block, platform or hole. Entity overlays stay separate.
internal static class SceneRegion
{
    internal const int MaxObjectGroups=256;
    internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    internal static readonly string[] HazardSources={"water","lava","honey","shimmer","unknown_liquid",
        "touch_damage","touch_hot","touch_bleeding","touch_destroy","boulder_tile","unsupported_motion_surface"};
    // Prepare generic codec/serializer metadata before the listener accepts game
    // queries. Synthetic detached records only: no Player, Tile or map access.
    internal static void Warmup()
    {
        var cells=new SceneCell[5,2];
        for(int y=0;y<2;y++)for(int x=0;x<5;x++)
            cells[x,y]=new(new("read",x%2==0,1,Solid:x%2==0),x%2==0?'#':'.',
                y==0?null:"ore_component",y==0?null:"Terraria/Copper",Sources:x%2==0?new[]{"lava"}:Array.Empty<string>());
        JsonSerializer.SerializeToUtf8Bytes(Encode(cells,c=>SceneGeometry.Geometry(c).ToString()),Json);
        JsonSerializer.SerializeToUtf8Bytes(Encode(cells,HazardMask),Json);
        JsonSerializer.SerializeToUtf8Bytes(Encode(cells,c=>new SceneKnowledge(c.Facts.Knowledge,c.Facts.CollisionKnowledge,c.Navigation!='?')),Json);
        JsonSerializer.SerializeToUtf8Bytes(Encode(cells,c=>new SceneRegionObjectCell(c.Kind??"",c.ContentKey)),Json);
    }
    internal static int HazardMask(SceneCell c)
    {
        if(c.Sources==null||c.Sources.Length==0)return 0;
        int mask=0;
        for(int i=0;i<HazardSources.Length;i++)if(c.Sources?.Contains(HazardSources[i])==true)mask|=1<<i;
        return mask;
    }
    internal static SceneRegionGrid<T> Encode<T>(SceneCell[,] cells,Func<SceneCell,T> project) where T:notnull
    {
        int w=cells.GetLength(0),h=cells.GetLength(1);
        var palette=new List<T>();var indices=new Dictionary<T,int>();var rows=new SceneRegionRow[h];
        for(int y=0;y<h;y++)
        {
            var raw=new int[w];
            for(int x=0;x<w;x++)
            {
                T value=project(cells[x,y]);
                if(!indices.TryGetValue(value,out int index)){index=palette.Count;indices.Add(value,index);palette.Add(value);}
                raw[x]=index;
            }
            var runs=new List<int>();
            for(int x=0;x<w;)
            {int end=x+1;while(end<w&&raw[end]==raw[x])end++;runs.Add(end-x);runs.Add(raw[x]);x=end;}
            // Numeric arrays are also limited by the existing Lua Worker node
            // budget. At most four runs per row; dense rows use fixed-width
            // ASCII palette indices, decoded with string.sub + tonumber(...,16).
            if(runs.Count<=8)rows[y]=new("rle",runs.ToArray());
            else
            {
                int digits=1,max=raw.Max();while((max>>=4)>0)digits++;
                var packed=new char[w*digits];const string hex="0123456789abcdef";
                for(int x=0;x<w;x++)for(int d=0;d<digits;d++)
                    packed[x*digits+d]=hex[(raw[x]>>((digits-d-1)*4))&15];
                rows[y]=new("hex_indices",new string(packed),digits);
            }
        }
        return new(2,palette.ToArray(),rows);
    }
}
