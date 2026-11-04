using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualPlayerEngineProbe;

public sealed record WorldCharacterManifest(
    [property:JsonRequired] int Version,[property:JsonRequired] string WorldUid,
    [property:JsonRequired] string CharacterId,[property:JsonRequired] string EngineHash,
    [property:JsonRequired] int SelectedItem,[property:JsonRequired] CharacterManifest Native, string MapHash=null);

// One world pointer commits native pair + hotbar together. Native generations
// remain immutable; a missing/corrupt pointer in an existing profile is NOT new.
public sealed class WorldCharacterFiles : IDisposable
{
    private static readonly JsonSerializerOptions Json=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    private readonly CharacterFiles files;
    private readonly string engineHash,directory,pointer;
    private string MigrationBackup => Path.Combine(directory, "world-before-engine-upgrade.json");
    private FileStream lease;
    public Guid World { get; }
    public string CharacterId { get; }
    public WorldCharacterFiles(string savePath,Guid world,string engine)
    {
        if(world==Guid.Empty)throw new IOException("Stable world UUID is unavailable; refusing a name-based profile.");
        World=world;engineHash=engine;files=new(savePath);CharacterId=IdFor(world);
        directory=files.DirectoryFor(CharacterId);pointer=Path.Combine(directory,"world.json");
    }
    public static string IdFor(Guid world)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("CompanionBridge/world-ai/v1/"+world.ToString("N"))))[..32].ToLowerInvariant();
    private static void Regular(string path)
    {if((File.GetAttributes(path)&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)throw new IOException("World character file is not a regular local file.");}
    public void Acquire()
    {
        if(lease!=null)throw new InvalidOperationException("World character lease already held.");
        files.DirectoryFor(CharacterId);Directory.CreateDirectory(directory);files.DirectoryFor(CharacterId);
        string path=Path.Combine(directory,"world.lock");
        if(Directory.EnumerateFileSystemEntries(directory).Any(p=>Path.GetFileName(p)=="world.lock"))Regular(path);
        lease=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    private void Held(){if(lease==null)throw new InvalidOperationException("World character lease is required.");files.DirectoryFor(CharacterId);}
    private static bool Digest(string value)=>value?.Length==64&&value.All(c=>c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private void Validate(WorldCharacterManifest data, string expectedEngine)
    {
        if(data==null||data.Version is not (1 or 2)||data.WorldUid!=World.ToString("N")||data.CharacterId!=CharacterId
            ||data.EngineHash!=expectedEngine||!Digest(data.EngineHash)||data.SelectedItem<0||data.SelectedItem>9||data.Native==null
            ||!Digest(data.Native.PlrHash)||!Digest(data.Native.TplrHash)||!Digest(data.Native.StateHash))
            throw new IOException("Unsupported, incomplete or wrong-world character manifest; no starter fallback.");
        if(data.Version==2&&!Digest(data.MapHash))throw new IOException("Missing map fingerprint.");
        CharacterFiles.ValidateId(data.Native.Generation);
    }
    public WorldCharacterManifest Read(string migrationSource = null)
    {
        Held();
        if(!PointerExists())
        {
            if(Directory.EnumerateFileSystemEntries(directory).Any(p=>Path.GetFileName(p)!="world.lock"))
                throw new IOException("Missing pointer in an existing/incomplete world character profile; no starter fallback.");
            return null;
        }
        return ReadPointer(pointer, engineHash, migrationSource);
    }

    private WorldCharacterManifest ReadPointer(string path, string expectedEngine, string alternate = null)
    {
        try
        {
            Regular(path);
            if(new FileInfo(path).Length>16384)throw new IOException("World character manifest exceeds its limit.");
            byte[] bytes=File.ReadAllBytes(path);
            using var parsed=JsonDocument.Parse(bytes);
            bool Unique(JsonElement element)=>element.ValueKind!=JsonValueKind.Object||element.EnumerateObject().Select(p=>p.Name).Distinct().Count()==element.EnumerateObject().Count()
                &&element.EnumerateObject().All(p=>Unique(p.Value));
            if(!Unique(parsed.RootElement))throw new IOException("Duplicate world character manifest keys.");
            var data=JsonSerializer.Deserialize<WorldCharacterManifest>(bytes,Json);
            Validate(data, alternate != null && data?.EngineHash == alternate ? alternate : expectedEngine);
            files.Open(CharacterId,data.Native);ValidateMap(data);return data;
        }
        catch(JsonException e){throw new IOException("Invalid world character manifest; existing files retained.",e);}
    }
    public string MapPath(WorldCharacterManifest data)=>Path.Combine(Path.GetDirectoryName(files.Open(CharacterId,data.Native).Path),"map.bin");
    private void ValidateMap(WorldCharacterManifest data)
    {if(data.Version==2&&CharacterFiles.Hash(MapPath(data))!=data.MapHash)throw new IOException("Map integrity check failed; no empty fallback.");}
    public string OpenMap(WorldCharacterManifest data)
    {Held();Validate(data,engineHash);ValidateMap(data);return data.Version==1?null:MapPath(data);}
    private bool PointerExists()=>Directory.EnumerateFileSystemEntries(directory).Any(p=>Path.GetFileName(p)=="world.json");
    public string Open(WorldCharacterManifest data)
    {Held();Validate(data,engineHash);return files.Open(CharacterId,data.Native).Path;}

    // The two native files and map can be carried across byte-for-byte. Native
    // read-back of the detached candidate is required before publishing it.
    public WorldCharacterManifest Migrate(WorldCharacterManifest source, string sourceEngine,
        Action<string, WorldCharacterManifest> verifyCandidate)
    {
        Held();
        ArgumentNullException.ThrowIfNull(verifyCandidate);
        if (sourceEngine == engineHash || source?.EngineHash != sourceEngine || Read(sourceEngine) != source)
            throw new IOException("Migration source changed or has the wrong engine.");
        var oldPath = files.Open(CharacterId, source.Native).Path;
        var newPath = files.Begin(CharacterId);
        Copy(oldPath, newPath);
        Copy(Path.ChangeExtension(oldPath, ".tplr"), Path.ChangeExtension(newPath, ".tplr"));
        if (source.Version == 2)
            Copy(Path.Combine(Path.GetDirectoryName(oldPath), "map.bin"), Path.Combine(Path.GetDirectoryName(newPath), "map.bin"));
        var candidate = source with { EngineHash = engineHash, Native = source.Native with { Generation = Path.GetFileName(Path.GetDirectoryName(newPath)) } };
        ValidateCandidate(candidate);
        verifyCandidate(newPath, candidate);
        ValidateCandidate(candidate);
        if (Read(sourceEngine) != source) throw new IOException("Migration source changed during verification.");
        if (File.Exists(MigrationBackup))
            Copy(MigrationBackup, RevisionBackup("upgrade"));
        Publish(candidate, replacing: true, backup: MigrationBackup);
        return candidate;

        static void Copy(string sourcePath, string destinationPath)
        {
            Regular(sourcePath);
            using var sourceStream = File.OpenRead(sourcePath);
            using var targetStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            sourceStream.CopyTo(targetStream);
            targetStream.Flush(true);
        }
    }

    public WorldCharacterManifest Rollback(string newerEngine, Action<string, WorldCharacterManifest> verifyCandidate)
    {
        Held();
        ArgumentNullException.ThrowIfNull(verifyCandidate);
        var current = ReadPointer(pointer, newerEngine);
        var previous = ReadPointer(MigrationBackup, engineHash);
        verifyCandidate(Open(previous), previous);
        if (ReadPointer(pointer, newerEngine) != current || ReadPointer(MigrationBackup, engineHash) != previous)
            throw new IOException("World character changed during rollback verification.");
        Publish(previous, replacing: true, backup: RevisionBackup("rollback"));
        return previous;
    }

    private string RevisionBackup(string operation) => Path.Combine(directory, $"world-before-{operation}-{CharacterFiles.NewId()}.json");

    private void ValidateCandidate(WorldCharacterManifest data)
    {
        Validate(data, engineHash);
        files.Open(CharacterId, data.Native);
        ValidateMap(data);
    }
    public WorldCharacterManifest Commit(string nativePath,string stateHash,int selected,WorldCharacterManifest expected,Action beforePublish=null,string mapHash=null)
    {
        Held();
        // Only a validated predecessor can be replaced; corruption or an
        // unexpected revision never gets overwritten by a later shutdown.
        var current=PointerExists()?Read():null;
        if(current!=expected)throw new IOException("World character pointer changed; refusing overwrite.");
        var data=new WorldCharacterManifest(mapHash==null?1:2,World.ToString("N"),CharacterId,engineHash,selected,files.Manifest(CharacterId,nativePath,stateHash),mapHash);
        ValidateCandidate(data);
        foreach(string native in new[]{nativePath,Path.ChangeExtension(nativePath,".tplr")})
            using(var stream=new FileStream(native,FileMode.Open,FileAccess.ReadWrite,FileShare.Read))stream.Flush(true);
        Publish(data, expected != null, beforePublish: beforePublish);
        return data;
    }

    private void Publish(WorldCharacterManifest data, bool replacing, string backup = null, Action beforePublish = null)
    {
        string temp=Path.Combine(directory,"world-"+CharacterFiles.NewId()+".tmp");
        using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {stream.Write(JsonSerializer.SerializeToUtf8Bytes(data,Json));stream.Flush(true);}
        beforePublish?.Invoke();
        if(replacing)File.Replace(temp,pointer,backup);else File.Move(temp,pointer);
    }
    public void Dispose(){lease?.Dispose();lease=null;}
}

public enum WorldCharacterPhase { Empty,Loading,Ready,Saving,Saved,Failed }
public sealed class WorldCharacterSession
{
    public Guid World { get; private set; }
    public string Run { get; private set; }
    public WorldCharacterPhase Phase { get; private set; }
    public bool Enter(Guid world,string run,Action initialize)
    {
        if(Phase!=WorldCharacterPhase.Empty)
        {
            if(World!=world||Run!=run)throw new IOException("World/session changed without a fresh runtime.");
            if(Phase is WorldCharacterPhase.Ready or WorldCharacterPhase.Saved)return false;
            throw new IOException("World character initialization failed or reentered; no retry.");
        }
        if(world==Guid.Empty||string.IsNullOrEmpty(run)||run=="none")throw new IOException("World character identity unavailable.");
        World=world;Run=run;Phase=WorldCharacterPhase.Loading;
        try{initialize();Phase=WorldCharacterPhase.Ready;return true;}
        catch{Phase=WorldCharacterPhase.Failed;throw;}
    }
    public bool Exit(Action save)
    {
        if(Phase is WorldCharacterPhase.Empty or WorldCharacterPhase.Saved)return false;
        if(Phase!=WorldCharacterPhase.Ready)throw new IOException("World character state is uncertain; refusing teardown/retry.");
        Phase=WorldCharacterPhase.Saving;
        try{save();Phase=WorldCharacterPhase.Saved;return true;}
        catch{Phase=WorldCharacterPhase.Failed;throw;}
    }
}
