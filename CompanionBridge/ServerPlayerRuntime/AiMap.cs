using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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
    private const int FileVersion=2;
    public const int ChunkSize=32;
    private readonly Guid session=Guid.NewGuid();
    private readonly Dictionary<(int X,int Y),Chunk> chunks=new();
    private long revision;
    private bool sampling,batchChanged;
    internal void BeginSample(){sampling=true;batchChanged=false;}
    internal void EndSample(){sampling=false;}
    private sealed class Chunk
    {
        internal long Revision,SeenTick=-1;
        internal Guid Session;
        internal readonly int[] Counts=new int[8];
    }
    internal long Revision=>revision;
    internal long ChunkRevision(int x,int y)=>chunks.GetValueOrDefault((x/ChunkSize,y/ChunkSize))?.Revision??0;
    public AiMap(Guid world,string character,int width,int height)
    {
        if(world==Guid.Empty||string.IsNullOrEmpty(character)||width<1||height<1||(long)width*height>40000000)throw new IOException("Invalid map identity/dimensions.");
        World=world;Character=character;Width=width;Height=height;cells=new byte[width*height];
    }
    public byte Get(int x,int y)=>x<0||y<0||x>=Width||y>=Height?(byte)0:cells[y*Width+x];
    public void Remember(int x,int y,byte value,long tick=-1)
    {
        if(x<0||y<0||x>=Width||y>=Height||value is <1 or >7)throw new ArgumentOutOfRangeException();
        var key=(x/ChunkSize,y/ChunkSize);
        if(!chunks.TryGetValue(key,out var chunk))chunks[key]=chunk=new();
        byte old=Get(x,y);
        if(old!=value)
        {
            if(!sampling || !batchChanged)revision++;
            batchChanged=true;
            if(old!=0)chunk.Counts[old]--;
            chunk.Counts[value]++;
            cells[y*Width+x]=value;
            chunk.Revision=revision;
        }
        chunk.Session=session;
        chunk.SeenTick=tick;
    }
    public void Save(string path)
    {
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var writer=new BinaryWriter(stream);
        writer.Write("CompanionBridge/AiMap");writer.Write(FileVersion);writer.Write(World.ToString("N"));writer.Write(Character);
        writer.Write(Width);writer.Write(Height);writer.Write(cells);
        writer.Write(revision);writer.Write(chunks.Count);
        foreach(var (key,chunk) in chunks)
        {
            writer.Write(key.X);writer.Write(key.Y);writer.Write(chunk.Revision);
            writer.Write(chunk.Session.ToByteArray());writer.Write(chunk.SeenTick);
        }
        writer.Flush();stream.Flush(true);
    }
    public static AiMap Load(string path,Guid world,string character,int width,int height)
    {
        long maxChunks=((width+31L)/32)*((height+31L)/32);
        if(new FileInfo(path).Length>(long)width*height+40*maxChunks+1012)throw new IOException("Map exceeds size limit.");
        using var reader=new BinaryReader(File.OpenRead(path));
        if(reader.ReadString()!="CompanionBridge/AiMap")throw new IOException("Invalid map header.");
        int format=reader.ReadInt32();
        if(format is not (1 or FileVersion)||reader.ReadString()!=world.ToString("N")||reader.ReadString()!=character
            ||reader.ReadInt32()!=width||reader.ReadInt32()!=height)throw new IOException("Incompatible map or identity mismatch.");
        var map=new AiMap(world,character,width,height);
        reader.BaseStream.ReadExactly(map.cells);
        for(int i=0;i<map.cells.Length;i++)
        {
            byte b=map.cells[i];
            if(b==0)continue;
            if(b>7)throw new IOException("Corrupt map cells.");
            var key=(i%width/ChunkSize,i/width/ChunkSize);
            if(!map.chunks.TryGetValue(key,out var chunk))map.chunks[key]=chunk=new();
            chunk.Counts[b]++;
        }
        if(format==FileVersion)
        {
            map.revision=reader.ReadInt64();
            int count=reader.ReadInt32();
            if(map.revision<0 || count!=map.chunks.Count)throw new IOException("Invalid chunk header.");
            var loaded=new HashSet<(int,int)>();
            for(int i=0;i<count;i++)
            {
                var key=(reader.ReadInt32(),reader.ReadInt32());
                if(!loaded.Add(key)||!map.chunks.TryGetValue(key,out var chunk))throw new IOException("Invalid chunk.");
                chunk.Revision=reader.ReadInt64();
                var id=reader.ReadBytes(16);
                if(id.Length!=16)throw new EndOfStreamException();
                chunk.Session=new Guid(id);chunk.SeenTick=reader.ReadInt64();
                if(chunk.Revision<0 || chunk.Revision>map.revision || chunk.SeenTick< -1)throw new IOException("Invalid chunk revision.");
            }
        }
        if(reader.BaseStream.Position!=reader.BaseStream.Length)throw new IOException("Trailing map data.");
        return map;
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
                int left=ox+c*size,top=oy+r*size,right=Math.Min(ox+w,left+size),bottom=Math.Min(oy+h,top+size);
                for(int y=top;y<bottom;)
                {
                    int endY=Math.Min(bottom,(y/ChunkSize+1)*ChunkSize);
                    for(int x=left;x<right;)
                    {
                        int endX=Math.Min(right,(x/ChunkSize+1)*ChunkSize);
                        if(!chunks.TryGetValue((x/ChunkSize,y/ChunkSize),out var chunk))mask|=1;
                        else if(x%ChunkSize==0 && y%ChunkSize==0 && endX-x==ChunkSize && endY-y==ChunkSize)
                        {
                            int count=0;
                            for(int i=1;i<=7;i++)if(chunk.Counts[i]>0){mask|=1<<i;count+=chunk.Counts[i];}
                            known+=count;if(count<ChunkSize*ChunkSize)mask|=1;
                        }
                        else
                            for(int yy=y;yy<endY;yy++)
                            for(int xx=x;xx<endX;xx++){byte b=Get(xx,yy);mask|=1<<b;if(b!=0)known++;}
                        x=endX;
                    }
                    y=endY;
                }
                // Mixed cells never collapse to solid/empty; liquid presence survives aggregation.
                line[c]=(mask&(1<<5))!=0?'L':(mask&(1<<7))!=0?'S':(mask&(1<<6))!=0?'H':(mask&(1<<4))!=0?'~':
                    (mask&(mask-1))!=0?':':symbols[System.Numerics.BitOperations.TrailingZeroCount((uint)mask)];
            }
            lines[r]=new string(line);
        }
        var result=new Dictionary<string,object>{
            ["schemaVersion"]=Version,["mode"]=mode,["worldUid"]=World.ToString("N"),["characterId"]=Character,
            ["originX"]=ox,["originY"]=oy,["cellSize"]=size,["width"]=w,["height"]=h,["rows"]=lines,
            ["exploredRatio"]=(double)known/(w*h),
            ["legend"]="? unknown; space known empty/nonblocking; # solid; = platform; : mixed or partially known; ~ water; L lava; H honey; S shimmer. Liquid marker means presence, not full coverage.",
            ["knowledge"]="last_known_state; not live; square Tile cells (+X right,+Y down); overview strategic only; region zoom with cellSize=1",
            ["visibility"]="server approximation v1: 120x68 actor viewport, opaque ray occlusion, surface daylight, underground radius 4, visible vanilla torches radius 14; no native lighting/mod light hooks"};
        if(mode=="region" && args.TryGetProperty("includeChunks",out var include) && include.GetBoolean())
        {
            var summary=new List<object>();
            for(int y=oy/ChunkSize;y<=(oy+h-1)/ChunkSize;y++)
            for(int x=ox/ChunkSize;x<=(ox+w-1)/ChunkSize;x++)
                if(chunks.TryGetValue((x,y),out var chunk))summary.Add(new{x,y,contentRevision=chunk.Revision,
                    knownCount=chunk.Counts.Sum(),lastSeenSession=chunk.Session,lastSeenTick=chunk.SeenTick});
            result["mapRevision"]=revision;result["observationSession"]=session;result["chunkSize"]=ChunkSize;
            result["worldWidth"]=Width;result["worldHeight"]=Height;result["chunks"]=summary;
        }
        return result;
    }
}
