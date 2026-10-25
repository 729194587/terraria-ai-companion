using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.IO;

namespace VirtualPlayerEngineProbe;

internal static class CharacterStore
{
    private static readonly PropertyInfo SaveErrors = typeof(Player).GetProperty("ModSaveErrors", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMemberException("Pinned engine ModSaveErrors property missing.");
    private static Player prepared, failedSave;
    internal static string CurrentId;
    internal static void Reset() { prepared = failedSave = null; CurrentId = null; CharacterIo.ExitRequests = 0; }
    internal static Player AcquireCandidate()
    {
        var p = prepared; prepared = null;
        if (p == null) CurrentId = null; // Explicit new actor must not overwrite the last loaded profile by default.
        return p;
    }
    internal static void AssertMayRetire(Player p)
    {
        if (p != null && ReferenceEquals(p, failedSave))
            throw new IOException("Last save failed; live Player retained. Save successfully before despawning/exiting.");
    }
    private static CharacterFiles Files => new(Main.SavePath);

    internal static string Inspect()
    {
        var p = ProbeSession.Actor ?? throw new InvalidOperationException("Spawn/load a Player first.");
        NetworkStateSync.Require(p);
        return $"life={p.statLife}/{p.statLifeMax}, mana={p.statMana}/{p.statManaMax}; " +
            $"item0={p.inventory[0].type}, item4={p.inventory[4].type}x{p.inventory[4].stack}, helmet={p.armor[0].type}, dye={p.dye[0].type}; " +
            $"marker={p.GetModPlayer<ProbePlayer>().PersistentMarker}; state={CharacterIo.State(p)}";
    }

    internal static string Save(string requestedId = null, bool injectFailure = false)
    {
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        lock (ProbeSession.Reservation.Gate)
        {
            var p = ProbeSession.Actor ?? throw new InvalidOperationException("Spawn/load a Player first.");
            NetworkStateSync.Require(p);
            failedSave = p;
            if (ProbeDebugAppearance.IsApplied(p)) throw new InvalidOperationException("Restore debug-style before saving character data.");
            string id = CharacterFiles.ValidateId(requestedId ?? CurrentId ?? CharacterFiles.NewId());
            try
            {
                var files = Files; var path = files.Begin(id);
                var before = CharacterIo.State(p);
                if (injectFailure) Directory.CreateDirectory(path); // Private uncommitted generation only.
                var data = new PlayerFileData(path, false) {
                    Player = p, Metadata = FileMetadata.FromCurrentSettings(FileType.Player)
                };
                if (Main.ServerSideCharacter || data.ServerSideCharacter) throw new IOException("Native character save is disabled in SSC mode.");
                CharacterIo.Run(data, () => { Player.SavePlayer(data, skipMapSave: true); return true; });
                if (SaveErrors.GetValue(p) is not Dictionary<string, string> errors || errors.Count != 0)
                    throw new IOException("Native ModPlayer save reported errors or unavailable diagnostics.");
                var restored = CharacterIo.Read(path);
                if (CharacterIo.State(p) != before || CharacterIo.State(restored) != before)
                    throw new IOException("Native character read-back differs; live Player and previous generation retained.");
                files.Commit(id, path, before);
                failedSave = null; CurrentId = id;
                ProbeSession.Log("character-saved", new { id, path, state = before, paired = true });
                return id;
            }
            catch { failedSave = p; throw; }
        }
    }

    internal static void Load(string requestedId)
    {
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        lock (ProbeSession.Reservation.Gate)
        {
            if (ProbeSession.Actor != null) throw new InvalidOperationException("Save and despawn the current Player before loading.");
            ProbeSession.CheckInitialSlot();
            string id = CharacterFiles.ValidateId(requestedId ?? CurrentId);
            var (path, manifest) = Files.Open(id);
            var p = CharacterIo.Read(path);
            if (CharacterIo.State(p) != manifest.StateHash) throw new IOException("Character state fingerprint differs from committed generation.");
            prepared = p;
            try
            {
                ProbeSession.Spawn();
                if (!ReferenceEquals(ProbeSession.Actor, p)) throw new IOException("Loaded candidate was not published.");
                CurrentId = id;
                ProbeSession.Log("character-loaded", new { id, path, state = manifest.StateHash });
            }
            finally { prepared = null; }
        }
    }

    internal static void TestSaveFailure()
    {
        var p = ProbeSession.Actor ?? throw new InvalidOperationException("Spawn a Player first.");
        bool menu = Main.gameMenu, disconnect = Netplay.Disconnect;
        long exitRequests = CharacterIo.ExitRequests;
        bool failed = false;
        try { Save(CharacterFiles.NewId(), injectFailure: true); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { failed = true; }
        if (!failed || !ReferenceEquals(p, ProbeSession.Actor) || !p.active || Main.gameMenu != menu
            || Netplay.Disconnect != disconnect || CharacterIo.ExitRequests != exitRequests)
            throw new InvalidOperationException("Native save-failure isolation did not pass.");
        ProbeSession.Log("character-save-failure-verified", new { retained = true, exitRequests = CharacterIo.ExitRequests });
    }
}
