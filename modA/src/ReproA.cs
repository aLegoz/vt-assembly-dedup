using HarmonyLib;
using UnityEngine;
using VoxelTycoon.Game.UI.Formatting;
using VoxelTycoon.Modding;

namespace VTDupRepro.A
{
    /// <summary>
    /// Minimal repro, mod A of two. Both mods postfix the same vanilla method
    /// (<see cref="Currency.Format"/>) and log whether the executing patch code can see the static
    /// state its own <c>Initialize</c> wrote. Expected (bug): shortly after both mods load, one of
    /// the logs flips to <c>initSeen=False</c> with a DIFFERENT assembly hash — the patch got
    /// re-resolved by Harmony into the duplicate assembly copy loaded by the main-menu pack scan,
    /// where Initialize never ran. No exceptions anywhere; state is just silently split.
    /// </summary>
    public class ReproAMod : Mod
    {
        internal static bool InitializedFlag;
        internal static int InitAsmHash;

        private Harmony _harmony;

        protected override void Initialize()
        {
            InitializedFlag = true;
            InitAsmHash = typeof(ReproAMod).Assembly.GetHashCode();
            _harmony = new Harmony("vtduprepro.a");
            _harmony.PatchAll(typeof(ReproAMod).Assembly);
            Debug.Log($"[ReproA] Initialize ran on assembly #{InitAsmHash:X}");
        }

        protected override void Deinitialize()
        {
            _harmony?.UnpatchAll("vtduprepro.a");
            _harmony = null;
        }
    }

    [HarmonyPatch(typeof(Currency), "Format")]
    internal static class ReproACurrencyPatch
    {
        private static string _lastLogged;

        private static void Postfix()
        {
            int patchAsm = typeof(ReproACurrencyPatch).Assembly.GetHashCode();
            string state = $"patchAsm=#{patchAsm:X} initSeen={ReproAMod.InitializedFlag} " +
                           $"initAsm=#{ReproAMod.InitAsmHash:X}";
            if (state == _lastLogged)
                return; // Format runs constantly — log state changes only
            _lastLogged = state;
            Debug.Log("[ReproA] " + state + (ReproAMod.InitializedFlag
                ? " (OK: patch and Initialize share one assembly copy)"
                : " (BUG: patch executes in a duplicate assembly copy where Initialize never ran)"));
        }
    }
}
