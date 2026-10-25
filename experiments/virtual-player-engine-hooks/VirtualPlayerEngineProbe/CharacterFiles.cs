using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace VirtualPlayerEngineProbe;

public sealed record CharacterManifest(string Generation, string PlrHash, string TplrHash, string StateHash);

// Each immutable generation contains a native .plr/.tplr pair. Only the small
// current.json pointer is replaced, after native read-back validation succeeds.
public sealed class CharacterFiles
{
    public string Root { get; }
    public CharacterFiles(string savePath) => Root = Path.GetFullPath(Path.Combine(savePath, "VirtualPlayerEngineProbe", "Characters"));
    public static string NewId() => Guid.NewGuid().ToString("N");
    public static string ValidateId(string id)
    {
        if (id == null || id.Length != 32 || !Guid.TryParseExact(id, "N", out var parsed) || parsed.ToString("N") != id)
            throw new ArgumentException("Character ID must be exactly 32 lowercase hexadecimal characters.");
        return id;
    }
    private void CheckPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full != Root && !full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Character path escaped the private root.");
        for (var item = new DirectoryInfo(full); item != null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Character storage cannot traverse a reparse point.");
    }
    public string DirectoryFor(string id)
    {
        var path = Path.Combine(Root, ValidateId(id)); CheckPath(path); return path;
    }
    public string Begin(string id)
    {
        var directory = DirectoryFor(id);
        Directory.CreateDirectory(directory); CheckPath(directory);
        // Fail before entering native SavePlayer, including on an unwritable root.
        var probe = Path.Combine(directory, "write-check-" + NewId());
        using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
            FileOptions.DeleteOnClose | FileOptions.WriteThrough)) { stream.WriteByte(1); stream.Flush(true); }
        var generation = Path.Combine(directory, NewId());
        Directory.CreateDirectory(generation); CheckPath(generation);
        return Path.Combine(generation, "character.plr");
    }
    private static void CheckFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Character storage cannot read a reparse-point file.");
    }
    public static string Hash(string path) { CheckFile(path); return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))); }
    public void Commit(string id, string plrPath, string stateHash)
    {
        var directory = DirectoryFor(id);
        CheckPath(Path.GetDirectoryName(plrPath));
        var generation = Path.GetFileName(Path.GetDirectoryName(plrPath)); ValidateId(generation);
        if (Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(plrPath))) != directory
            || Path.GetFileName(plrPath) != "character.plr") throw new IOException("Unexpected staged character path.");
        var manifest = new CharacterManifest(generation, Hash(plrPath), Hash(Path.ChangeExtension(plrPath, ".tplr")), stateHash);
        var temp = Path.Combine(directory, "current-" + NewId() + ".tmp");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest));
            stream.Write(bytes); stream.Flush(true);
        }
        File.Move(temp, Path.Combine(directory, "current.json"), true);
    }
    public (string Path, CharacterManifest Manifest) Open(string id)
    {
        var directory = DirectoryFor(id);
        var pointer = Path.Combine(directory, "current.json"); CheckFile(pointer);
        var manifest = JsonSerializer.Deserialize<CharacterManifest>(File.ReadAllText(pointer))
            ?? throw new IOException("Missing character manifest.");
        var generation = Path.Combine(directory, ValidateId(manifest.Generation)); CheckPath(generation);
        var path = Path.Combine(generation, "character.plr");
        if (Hash(path) != manifest.PlrHash || Hash(Path.ChangeExtension(path, ".tplr")) != manifest.TplrHash)
            throw new IOException("Native character pair failed integrity validation; previous generations retained.");
        return (path, manifest);
    }
}
