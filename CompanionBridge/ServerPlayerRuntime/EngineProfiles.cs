using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Terraria;
using Terraria.ModLoader;

namespace VirtualPlayerEngineProbe;

internal sealed class EngineProfiles
{
    internal const string Previous = "2026.08.3.0";
    internal const string Target = "2026.09.3.1";

    private readonly JsonElement profiles;
    internal string Selected { get; }
    internal string EngineHash => HashFor(Selected);

    private EngineProfiles(JsonElement profiles, string selected)
    {
        this.profiles = profiles;
        Selected = selected;
    }

    internal string HashFor(string name) => profiles.GetProperty(name).GetProperty("engineHash").GetString();

    internal static EngineProfiles Verify(Mod mod)
    {
        using var document = JsonDocument.Parse(mod.GetFileBytes("EngineProfiles.json"));
        var root = document.RootElement;
        var profiles = root.GetProperty("profiles");
        string selected = Environment.GetEnvironmentVariable("COMPANION_ENGINE_PROFILE");
        if (Program.LaunchParameters.TryGetValue("-companionengineprofile", out var argument)) selected = argument;
        // Existing launchers can select by the exact normalized version. Explicit
        // selection additionally prevents a launcher from using the wrong install.
        selected ??= profiles.EnumerateObject().SingleOrDefault(profile =>
            profile.Value.GetProperty("tml").GetString() == BuildInfo.tMLVersion.ToString()).Name;
        if (selected == null || !profiles.TryGetProperty(selected, out var profile))
            throw new InvalidOperationException("Unsupported engine profile; no hooks installed.");
        string Value(string key) => profile.GetProperty(key).GetString();
        static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        if (typeof(Main).Assembly.GetName().Version?.ToString() != Value("terraria")
            || BuildInfo.tMLVersion.ToString() != Value("tml") || BuildInfo.CommitSHA != Value("commit")
            || Hash(typeof(Main).Assembly.Location) != Value("engineHash")
            || Hash(typeof(On_Netplay).Assembly.Location) != Value("hooksHash"))
            throw new InvalidOperationException("Unsupported engine/hooks fingerprint; no hooks installed.");
        return new(profiles.Clone(), selected);
    }
}
