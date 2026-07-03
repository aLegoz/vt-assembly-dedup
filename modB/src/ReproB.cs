using HarmonyLib;
using UnityEngine;
using VoxelTycoon.Game.UI.Formatting;
using VoxelTycoon.Modding;

namespace VTDupRepro.B
{
    /// <summary>Mod B of the minimal repro pair — identical to A except for names/ids. Whichever of
    /// the two mods patched <see cref="Currency.Format"/> FIRST gets its patch re-resolved into the
    /// stale duplicate assembly when the second one patches, and its log flips to initSeen=False.</summary>
    public class ReproBMod : Mod
    {
        internal static bool InitializedFlag;
        internal static int InitAsmHash;

        private Harmony _harmony;

        protected override void Initialize()
        {
            InitializedFlag = true;
            InitAsmHash = typeof(ReproBMod).Assembly.GetHashCode();
            _harmony = new Harmony("vtduprepro.b");
            _harmony.PatchAll(typeof(ReproBMod).Assembly);
            Debug.Log($"[ReproB] Initialize ran on assembly #{InitAsmHash:X}");
        }

        protected override void Deinitialize()
        {
            _harmony?.UnpatchAll("vtduprepro.b");
            _harmony = null;
        }
    }

    [HarmonyPatch(typeof(Currency), "Format")]
    internal static class ReproBCurrencyPatch
    {
        private static string _lastLogged;

        private static void Postfix()
        {
            int patchAsm = typeof(ReproBCurrencyPatch).Assembly.GetHashCode();
            string state = $"patchAsm=#{patchAsm:X} initSeen={ReproBMod.InitializedFlag} " +
                           $"initAsm=#{ReproBMod.InitAsmHash:X}";
            if (state == _lastLogged)
                return;
            _lastLogged = state;
            Debug.Log("[ReproB] " + state + (ReproBMod.InitializedFlag
                ? " (OK: patch and Initialize share one assembly copy)"
                : " (BUG: patch executes in a duplicate assembly copy where Initialize never ran)"));
        }
    }
}
