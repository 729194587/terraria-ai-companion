using System;
using System.IO;
using System.IO.Compression;
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
    private const int FileVersion=4;
    public const int ChunkSize=32;
    private readonly Guid session=Guid.NewGuid();
    private readonly Dictionary<(int X,int Y),Chunk> chunks=new();
    // Recent foot tiles, scoped to this loaded character/world. Seeing a tile
    // and walking through it are independent observations.
    private readonly Queue<(int,int)> recentFootTiles=new();
    private readonly Dictionary<(int,int),int> footCounts=new();
    private (int,int)? lastFoot;
    // One bit per actual foot tile. Visits can precede the first light sample.
    private readonly Dictionary<(int,int),byte[]> visits=new();
    internal int VisitedCount { get; private set; }
    internal bool Visited(int x,int y)
    {
        if(x<0 || x>=Width || y<0 || y>=Height ||
            !visits.TryGetValue((x/ChunkSize,y/ChunkSize),out var bits))return false;
        int offset=y%ChunkSize*ChunkSize+x%ChunkSize;
        return (bits[offset/8] & (1<<(offset%8)))!=0;
    }
    internal void RecordFeet(int x,int y)
    {
        if(lastFoot==(x,y) || x<0 || y<0 || x>=Width || y>=Height)return;
        var key=(x/ChunkSize,y/ChunkSize);
        if(!visits.TryGetValue(key,out var bits))visits[key]=bits=new byte[ChunkSize*ChunkSize/8];
        int offset=y%ChunkSize*ChunkSize+x%ChunkSize;
        if(!Visited(x,y)){bits[offset/8]|=(byte)(1<<(offset%8));VisitedCount++;}
        lastFoot=(x,y);
        recentFootTiles.Enqueue((x,y));
        footCounts[(x,y)]=footCounts.GetValueOrDefault((x,y))+1;
        if(recentFootTiles.Count<=4096)return;
        var oldest=recentFootTiles.Dequeue();
        if(--footCounts[oldest]==0)footCounts.Remove(oldest);
    }
    internal bool WalkedNear(int x,int y)
    {
        for(int i=x-1;i<=x+1;i++)for(int j=y-1;j<=y+1;j++)
            if(footCounts.ContainsKey((i,j)))return true;
        return false;
    }
    internal int UnknownAround(int x,int y)
    {
        int count=0;
        for(int i=Math.Max(0,x-4);i<=Math.Min(Width-1,x+4);i++)
        for(int j=Math.Max(0,y-4);j<=Math.Min(Height-1,y+4);j++)
            if(Get(i,j)==0)count++;
        return count;
    }
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
        writer.Write(Width);writer.Write(Height);
        long lengthAt=stream.Position;
        writer.Write(0);
        using(var compressed=new BrotliStream(stream,CompressionLevel.Fastest,leaveOpen:true))
            compressed.Write(cells);
        long end=stream.Position;
        stream.Position=lengthAt;
        writer.Write(checked((int)(end-lengthAt-sizeof(int))));
        stream.Position=end;
        writer.Write(revision);writer.Write(chunks.Count);
        foreach(var (key,chunk) in chunks)
        {
            writer.Write(key.X);writer.Write(key.Y);writer.Write(chunk.Revision);
            writer.Write(chunk.Session.ToByteArray());writer.Write(chunk.SeenTick);
        }
        writer.Write(visits.Count);
        foreach(var ((cx,cy),bits) in visits)
        {writer.Write(cx);writer.Write(cy);writer.Write(bits);}
        writer.Flush();stream.Flush(true);
    }
    public static AiMap Load(string path,Guid world,string character,int width,int height)
    {
        long maxChunks=((width+31L)/32)*((height+31L)/32);
        long cellCount=(long)width*height;
        if(width<1||height<1||cellCount>40000000)throw new IOException("Invalid map dimensions.");
        int maxPacked=BrotliEncoder.GetMaxCompressedLength((int)cellCount);
        if(new FileInfo(path).Length>Math.Max(cellCount,maxPacked)+(40+136)*maxChunks+1016)throw new IOException("Map exceeds size limit.");
        using var reader=new BinaryReader(File.OpenRead(path));
        if(reader.ReadString()!="CompanionBridge/AiMap")throw new IOException("Invalid map header.");
        int format=reader.ReadInt32();
        if(format is not (1 or 2 or 3 or FileVersion)||reader.ReadString()!=world.ToString("N")||reader.ReadString()!=character
            ||reader.ReadInt32()!=width||reader.ReadInt32()!=height)throw new IOException("Incompatible map or identity mismatch.");
        var map=new AiMap(world,character,width,height);
        if(format>=3)
        {
            int length=reader.ReadInt32();
            if(length<1||length>maxPacked||length>reader.BaseStream.Length-reader.BaseStream.Position)
                throw new IOException("Invalid compressed map length.");
            var packed=reader.ReadBytes(length);
            using var decoder=new BrotliDecoder();
            if(decoder.Decompress(packed,map.cells,out int consumed,out int written)!=System.Buffers.OperationStatus.Done
                ||consumed!=length||written!=map.cells.Length)throw new IOException("Invalid compressed map cells.");
        }
        else reader.BaseStream.ReadExactly(map.cells);
        for(int i=0;i<map.cells.Length;i++)
        {
            byte b=map.cells[i];
            if(b==0)continue;
            if(b>7)throw new IOException("Corrupt map cells.");
            var key=(i%width/ChunkSize,i/width/ChunkSize);
            if(!map.chunks.TryGetValue(key,out var chunk))map.chunks[key]=chunk=new();
            chunk.Counts[b]++;
        }
        if(format>=2)
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
        if(format>=4)
        {
            int count=reader.ReadInt32();
            if(count<0 || count>maxChunks)throw new IOException("Invalid visit count.");
            for(int i=0;i<count;i++)
            {
                int cx=reader.ReadInt32(),cy=reader.ReadInt32();
                var bits=reader.ReadBytes(ChunkSize*ChunkSize/8);
                if(cx<0 || cx>(width-1)/ChunkSize || cy<0 || cy>(height-1)/ChunkSize ||
                    bits.Length!=ChunkSize*ChunkSize/8 || !map.visits.TryAdd((cx,cy),bits))
                    throw new IOException("Invalid visit chunk.");
                foreach(byte value in bits)map.VisitedCount+=System.Numerics.BitOperations.PopCount(value);
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
            ["visibility"]="119x67 actor viewport; lit terrain shared with look_around; past observations retained; recent foot positions tracked separately"};
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
        if(mode=="region" && args.TryGetProperty("includeVisits",out var flag) && flag.GetBoolean())
        {
            var bits=new byte[(w*h+7)/8];
            for(int y=0;y<h;y++)
            for(int x=0;x<w;x++)
            {
                int offset=y*w+x;
                if(Visited(ox+x,oy+y))bits[offset/8]|=(byte)(1<<(offset%8));
            }
            result["visitBits"]=Convert.ToBase64String(bits);
            result["visitedCount"]=VisitedCount;
        }
        return result;
    }
}
