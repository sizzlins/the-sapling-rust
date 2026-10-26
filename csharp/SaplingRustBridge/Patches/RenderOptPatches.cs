using System;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Rendering and memory optimization patches:
    ///
    /// 1. OBJECT POOLING: Ensure CopiedOrganism component is attached to all cloned
    ///    organism instances so vanilla OrganismObjectPool reclaims them upon cleanup.
    ///
    /// 2. GPU INSTANCING: Enable instancing on organism materials so creatures of
    ///    the same species batch into unified draw calls.
    /// </summary>
    [HarmonyPatch]
    public static class RenderOptPatches
    {
        // ── 1. OBJECT POOLING INTEGRATION ──

        /// <summary>
        /// Ensure any clone retrieved from the library has CopiedOrganism attached
        /// so that CleanUpAnimalGameObject recycles it into the pool instead of Destroying.
        /// </summary>
        [HarmonyPatch(typeof(PrebuiltOrganismLibrary), nameof(PrebuiltOrganismLibrary.GetPrebuiltOrganismProfileBased))]
        [HarmonyPostfix]
        public static void EnsureCopiedOrganism_Postfix(
            ref GameObject __result,
            int definitionIndex,
            int sexIndex,
            bool youngVersion)
        {
            if (__result == null) return;

            var co = __result.GetComponent<CopiedOrganism>();
            if (co == null)
            {
                co = __result.AddComponent<CopiedOrganism>();
                co.definitionIndex = definitionIndex;
                co.youngVersion = youngVersion;
                co.sexIndex = sexIndex;
            }
        }

        // ── 2. GPU INSTANCING ──

        private static bool _instancingApplied = false;

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.ShowState))]
        [HarmonyPostfix]
        public static void ShowState_EnableInstancing(WorldView __instance)
        {
            if (_instancingApplied) return;
            _instancingApplied = true;

            EnableInstancingOnAllRenderers();
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.InitializeAnimalView),
            new Type[] { typeof(AnimalView), typeof(AnimalDefinition), typeof(Vector3),
                         typeof(CellLocationType), typeof(bool) })]
        [HarmonyPostfix]
        public static void InitAnimalView_EnableInstancing(AnimalView view, bool JITOptimization)
        {
            if (JITOptimization || view == null || view.gameObject == null) return;
            EnableInstancingOnGameObject(view.gameObject);
        }

        private static void EnableInstancingOnAllRenderers()
        {
            try
            {
                var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
                int count = 0;
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    if (mats == null) continue;
                    for (int j = 0; j < mats.Length; j++)
                    {
                        if (mats[j] != null && !mats[j].enableInstancing)
                        {
                            mats[j].enableInstancing = true;
                            count++;
                        }
                    }
                }
                if (count > 0)
                {
                    UnityEngine.Debug.LogWarning(
                        "[SaplingRust] Enabled GPU instancing on " + count + " materials");
                }
            }
            catch { }
        }

        private static void EnableInstancingOnGameObject(GameObject go)
        {
            if (go == null) return;
            try
            {
                var renderers = go.GetComponentsInChildren<Renderer>(true);
                if (renderers == null) return;
                for (int i = 0; i < renderers.Length; i++)
                {
                    var mats = renderers[i].sharedMaterials;
                    if (mats == null) continue;
                    for (int j = 0; j < mats.Length; j++)
                    {
                        if (mats[j] != null && !mats[j].enableInstancing)
                        {
                            mats[j].enableInstancing = true;
                        }
                    }
                }
            }
            catch { }
        }
    }
}
