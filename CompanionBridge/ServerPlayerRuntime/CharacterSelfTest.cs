using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace VirtualPlayerEngineProbe;

// Explicit diagnostic fixtures only; never part of normal character initialization.
internal static class CharacterSelfTest
{
    private static bool ran;
    internal static void Seed()
    {
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        var p = ProbeSession.Actor ?? throw new InvalidOperationException("Spawn a Player first.");
        NetworkStateSync.Require(p);
        if (ProbeDebugAppearance.IsApplied(p)) throw new InvalidOperationException("Restore debug-style first.");
        p.inventory[0].SetDefaults(ItemID.CopperPickaxe);
        p.inventory[4].SetDefaults(ItemID.Torch); p.inventory[4].stack = 37;
        p.armor[0].SetDefaults(ItemID.IronHelmet);
        p.dye[0].SetDefaults(ItemID.RedDye);
        p.shirtColor = new Color(71, 129, 183); p.hairColor = new Color(121, 53, 28);
        p.hair = 3; p.skinVariant = 0;
        p.ConsumedLifeCrystals = 2; p.ConsumedLifeFruit = 0; p.ConsumedManaCrystals = 2;
        p.statLifeMax = p.statLifeMax2 = 140; p.statLife = 117;
        p.statManaMax = p.statManaMax2 = 60; p.statMana = 43;
        p.GetModPlayer<ProbePlayer>().PersistentMarker = "g31-" + Guid.NewGuid().ToString("N");
        NetworkStateSync.Full(p);
        ProbeSession.Log("character-fixture-applied", new { state = CharacterIo.State(p) });
    }

    internal static void Update()
    {
        if (ran || !Main.dedServ || ProbeSession.Reservation.Snapshot == null
            || !Environment.GetCommandLineArgs().Contains("-g31selftest")) return;
        ran = true;
        string report = Path.Combine(Main.SavePath, "g31-native-result.json");
        try
        {
            var args = Environment.GetCommandLineArgs();
            int resume = Array.IndexOf(args, "-g31loadid");
            if (resume >= 0) CharacterStore.Load(args[resume + 1]);
            else { ProbeSession.Spawn(); Seed(); }
            var before = CharacterIo.State(ProbeSession.Actor);
            var scopedFile = new Terraria.IO.PlayerFileData("scope-test", false) { Player = ProbeSession.Actor };
            var alias = new Terraria.IO.PlayerFileData("scope-test", false) { Player = ProbeSession.Actor };
            CharacterIo.Run(scopedFile, () => {
                if (!CharacterIo.IsCharacterFile(scopedFile) || CharacterIo.IsCharacterFile(alias))
                    throw new IOException("Character file ownership predicate accepted an alias.");
                return true;
            });
            if (CharacterIo.IsCharacterFile(scopedFile)) throw new IOException("Character save scope leaked.");
            CharacterIo.Run(null, () => {
                if (CharacterIo.IsCharacterFile(null)) throw new IOException("Load scope authorized a null save file.");
                return true;
            });
            string id = CharacterStore.Save();
            for (int i = 0; i < 2; i++)
            {
                var old = ProbeSession.Actor;
                ProbeSession.Finish("native persistence selftest", keepReservation: true);
                CharacterStore.Load(id);
                if (ReferenceEquals(old, ProbeSession.Actor) || CharacterIo.State(ProbeSession.Actor) != before)
                    throw new IOException("Published round-trip did not preserve character data.");
                CharacterStore.Save(id);
            }
            ProbeDebugAppearance.Toggle(ProbeSession.Actor);
            bool rejected = false;
            try { CharacterStore.Save(); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new IOException("Debug overlay was persisted.");
            ProbeDebugAppearance.Toggle(ProbeSession.Actor);
            CharacterStore.Save();
            CharacterStore.TestSaveFailure();
            bool retained = false;
            try { ProbeSession.Finish("must refuse failed save", keepReservation: true); }
            catch (IOException) { retained = true; }
            if (!retained) throw new IOException("Failed save allowed live state destruction.");
            CharacterStore.Save();
            // Native load may return a fallback Player instead of throwing. It
            // must never be accepted as a valid persisted character.
            var corrupt = new CharacterFiles(Main.SavePath).Begin(CharacterFiles.NewId());
            File.WriteAllBytes(corrupt, new byte[] { 1, 2, 3, 4 });
            bool badLoadRejected = false;
            try { CharacterIo.Read(corrupt); } catch (IOException) { badLoadRejected = true; }
            if (!badLoadRejected) throw new IOException("Corrupt native character was accepted.");
            ProbeSession.Finish("selftest complete", keepReservation: true);
            File.WriteAllText(report, JsonSerializer.Serialize(new { result = "native-persistence-selftest-passed",
                id, roundTrips = 2, restoredFromPriorProcess = resume >= 0, corruptLoadRejected = badLoadRejected,
                saveFailureRetained = retained, exitRequests = CharacterIo.ExitRequests,
                hostAndPlay = "not executed", remoteMultiplayer = "not executed" }));
        }
        catch (Exception e)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { result = "failed", error = e.ToString() }));
            ProbeSession.Log("character-selftest-failed", e.ToString());
            // Keep the failed Player alive for diagnosis; no automatic teardown.
            return;
        }
        Netplay.Disconnect = true;
    }
}
