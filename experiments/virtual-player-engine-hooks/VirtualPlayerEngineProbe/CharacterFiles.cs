using System;
using System.IO;
using System.Linq;
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
    public string[] KnownIds()
    {
        CheckPath(Root);
        if (!Directory.Exists(Root)) return Array.Empty<string>();
        return Directory.GetDirectories(Root).Select(Path.GetFileName)
            .Where(id => id.Length == 32 && Guid.TryParseExact(id, "N", out var guid) && guid.ToString("N") == id)
            .Where(id => { var directory = DirectoryFor(id); return File.Exists(Path.Combine(directory, "current.json")); })
            .OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }
    public string Resolve(string selector)
    {
        if (selector?.Length == 32) return ValidateId(selector);
        if (selector != null && (selector.Length < 6 || selector.Length > 32
            || selector.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))))
            throw new ArgumentException("Use a profile's short ID (at least 6 lowercase hex characters).");
        var matches = KnownIds().Where(id => selector == null || id.StartsWith(selector, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) throw new IOException(matches.Length == 0 ? "No matching saved profile. Use /vph profiles."
            : "Multiple profiles match. Use /vph profiles and specify a longer ID.");
        return matches[0];
    }
    public string ShortId(string id)
    {
        ValidateId(id);
        var ids = KnownIds();
        int length = 6;
        while (length < 32 && ids.Count(other => other.StartsWith(id.Substring(0, length), StringComparison.Ordinal)) > 1) length++;
        return id.Substring(0, length);
    }
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
