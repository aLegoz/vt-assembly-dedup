using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VoxelTycoon.Modding;

namespace VTDedupFix
{
    /// <summary>
    /// Runtime proof-of-concept of the loader fix proposed to the developer: deduplicate mod
    /// assemblies. The game's <c>ModLoader.LoadAssembly</c> uses <c>Assembly.Load(bytes)</c>, which
    /// creates a NEW unloadable assembly copy per call — and every launch scans each pack DLL at
    /// least twice (main-menu scan + game load), plus once more per new game/save in the same
    /// process. Duplicate copies split type identity: Harmony re-resolves serialized patch methods
    /// into the FIRST copy while <c>Mod.Initialize</c> runs on the LAST, silently breaking any mod
    /// that checks its own types inside patch code.
    ///
    /// <para>The fix: a prefix on <c>ModLoader.LoadAssembly</c> that returns the already-loaded
    /// assembly with the same <see cref="AssemblyName.FullName"/> instead of loading a duplicate.
    /// Installed from <see cref="MainMenuMod.Initialize"/>, which runs during the very first (menu)
    /// scan — so the game-load scan and everything after it reuses the menu copies, and every mod's
    /// patches, registrations and statics live in ONE assembly identity.</para>
    ///
    /// <para>Trade-off (same as the game would have with a real cache): replacing a mod's DLL on
    /// disk without restarting the game no longer produces a new copy — which today never worked
    /// reliably anyway, precisely because of this bug.</para>
    /// </summary>
    public class DedupFixMainMenuMod : MainMenuMod
    {
        protected override void Initialize() => LoaderDedup.Install();
    }

    /// <summary>Safety net for the pack being enabled per-save only (no menu scan): installs from
    /// the regular mod lifecycle instead. Later than ideal (this cycle's scan already ran), but
    /// covers every following cycle.</summary>
    public class DedupFixMod : Mod
    {
        protected override void Initialize() => LoaderDedup.Install();
    }

    internal static class LoaderDedup
    {
        private const string HarmonyId = "tvvtm.dedupfix";

        public static void Install()
        {
            Harmony harmony = new Harmony(HarmonyId);
            // Idempotent: Install is called from several hooks and after every wipe (see below).
            harmony.UnpatchAll(HarmonyId);
            harmony.Patch(
                AccessTools.Method(typeof(ModLoader), "LoadAssembly"),
                prefix: new HarmonyMethod(typeof(LoaderDedup), nameof(LoadAssemblyPrefix)));
            // BOTH mod managers end their OnDeinitialize with
            // `new Harmony("voxeltycoon.harmony").UnpatchAll(null)`, wiping EVERY mod's patches,
            // ours included: MainMenuModManager on leaving the menu (i.e. right BEFORE the game's
            // mod scan!), ModManager on leaving a game. Re-install right after from these
            // postfixes: the still-running compiled replacement contains them, so the fix
            // survives into the next scan.
            harmony.Patch(
                AccessTools.Method(typeof(ModManager), "OnDeinitialize"),
                postfix: new HarmonyMethod(typeof(LoaderDedup), nameof(ResurrectPostfix)));
            harmony.Patch(
                AccessTools.Method(typeof(MainMenuModManager), "OnDeinitialize"),
                postfix: new HarmonyMethod(typeof(LoaderDedup), nameof(ResurrectPostfix)));
            Debug.Log("[VTDedupFix] Loader dedup installed.");
        }

        /// <summary>Copy-proof by construction: touches no mod-defined types, so it behaves
        /// identically no matter which assembly copy Harmony resolves it into.</summary>
        private static bool LoadAssemblyPrefix(string path, ref Assembly __result)
        {
            AssemblyName name;
            try
            {
                name = AssemblyName.GetAssemblyName(path); // metadata only — no load, no lock
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VTDedupFix] Could not read assembly name of " + path + ": " + e.Message);
                return true; // let the original decide
            }

            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (loaded.FullName == name.FullName)
                {
                    __result = loaded;
                    Debug.Log($"[VTDedupFix] Reusing already-loaded {Path.GetFileName(path)} " +
                              $"(#{loaded.GetHashCode():X}) instead of a duplicate copy.");
                    return false;
                }
            }
            return true;
        }

        private static void ResurrectPostfix() => Install();
    }
}
