using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VirtualPlayerEngineProbe;

// No Terraria dependencies: queries can only read remembered cells.
public sealed class AiMap
{
    public const int Version=1;
    public Guid World { get; }
    public string Character { get; }
    public int Width { get; }
    public int Height { get; }
    private readonly byte[] cells;
    public AiMap(Guid world,string character,int width,int height)
    {
        if(world==Guid.Empty||string.IsNullOrEmpty(character)||width<1||height<1||(long)width*height>40000000)throw new IOException("Invalid map identity/dimensions.");
        World=world;Character=character;Width=width;Height=height;cells=new byte[width*height];
    }
    public byte Get(int x,int y)=>x<0||y<0||x>=Width||y>=Height?(byte)0:cells[y*Width+x];
    public void Remember(int x,int y,byte value)
    {if(x<0||y<0||x>=Width||y>=Height||value is <1 or >7)throw new ArgumentOutOfRangeException();cells[y*Width+x]=value;}
    public void Save(string path)
    {
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var writer=new BinaryWriter(stream);
        writer.Write("CompanionBridge/AiMap");writer.Write(Version);writer.Write(World.ToString("N"));writer.Write(Character);
        writer.Write(Width);writer.Write(Height);writer.Write(cells);writer.Flush();stream.Flush(true);
    }
    public static AiMap Load(string path,Guid world,string character,int width,int height)
    {
        if(new FileInfo(path).Length>40001000)throw new IOException("Map exceeds size limit.");
        using var reader=new BinaryReader(File.OpenRead(path));
        if(reader.ReadString()!="CompanionBridge/AiMap"||reader.ReadInt32()!=Version||reader.ReadString()!=world.ToString("N")||reader.ReadString()!=character
            ||reader.ReadInt32()!=width||reader.ReadInt32()!=height)throw new IOException("Incompatible map or identity mismatch.");
        var map=new AiMap(world,character,width,height);var bytes=reader.ReadBytes(map.cells.Length);
        if(bytes.Length!=map.cells.Length||reader.BaseStream.Position!=reader.BaseStream.Length||bytes.Any(b=>b>7))throw new IOException("Corrupt map cells.");
        Array.Copy(bytes,map.cells,bytes.Length);return map;
    }
    public object Query(JsonElement args,int actorX,int actorY)
    {
        string mode=args.GetProperty("mode").GetString();
        int Read(string name,int fallback)=>args.TryGetProperty(name,out var value)?value.GetInt32():fallback;
        int size=mode=="overview"?Math.Max(1,Math.Max((Width+127)/128,(Height+47)/48)):Read("cellSize",2);
        int w=mode=="overview"?Width:Read("width",120),h=mode=="overview"?Height:Read("height",68);
        int ox=mode=="overview"?0:Math.Clamp(Read("centerX",actorX)-w/2,0,Math.Max(0,Width-w));
        int oy=mode=="overview"?0:Math.Clamp(Read("centerY",actorY)-h/2,0,Math.Max(0,Height-h));
        w=Math.Min(w,Width-ox);h=Math.Min(h,Height-oy);
        int cols=(w+size-1)/size,rows=(h+size-1)/size;var lines=new string[rows];long known=0;
        const string symbols="? #=~LHS";
        for(int r=0;r<rows;r++)
        {
            var line=new char[cols];
            for(int c=0;c<cols;c++)
            {
                int mask=0;
                for(int y=oy+r*size;y<Math.Min(oy+h,oy+(r+1)*size);y++)
                for(int x=ox+c*size;x<Math.Min(ox+w,ox+(c+1)*size);x++)
                {byte b=Get(x,y);mask|=1<<b;if(b!=0)known++;}
                // Mixed cells never collapse to solid/empty; liquid presence survives aggregation.
                line[c]=(mask&(1<<5))!=0?'L':(mask&(1<<7))!=0?'S':(mask&(1<<6))!=0?'H':(mask&(1<<4))!=0?'~':
                    (mask&(mask-1))!=0?':':symbols[System.Numerics.BitOperations.TrailingZeroCount((uint)mask)];
            }
            lines[r]=new string(line);
        }
        return new{schemaVersion=Version,mode,worldUid=World.ToString("N"),characterId=Character,originX=ox,originY=oy,cellSize=size,width=w,height=h,rows=lines,
            exploredRatio=(double)known/(w*h),legend="? unknown; space known empty/nonblocking; # solid; = platform; : mixed or partially known; ~ water; L lava; H honey; S shimmer. Liquid marker means presence, not full coverage.",
            knowledge="last_known_state; not live; square Tile cells (+X right,+Y down); overview strategic only; region zoom with cellSize=1",
            visibility="server approximation v1: 120x68 actor viewport, opaque ray occlusion, surface daylight, underground radius 4, visible vanilla torches radius 14; no native lighting/mod light hooks"};
    }
}
