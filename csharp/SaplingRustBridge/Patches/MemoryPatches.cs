using System;
using HarmonyLib;
using UnityEngine;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Memory governor and stability patches for The Sapling.
    /// 1. Shares AnimalTextureFactory across builders to eliminate duplicate GPU textures.
    /// 2. Periodic extinct-definition pruning.
    /// 3. Frees leaked red-flash Materials created whenever an animal takes damage.
    /// 4. Slims the historical timeline while loading a save (fixes the multi-GB load OOM).
    /// 5. Camera focus guards prevent NullReferenceException when focusing on organisms.
    /// </summary>
    [HarmonyPatch]
    public static class MemoryPatches
    {
        public static AnimalTextureFactory SharedAnimalTextureFactory = null;
        private static int _dayCounter = 0;
        private static float _lastTrimTime = 0f;

        // --- 1. CAPTURE SHARED TEXTURE FACTORY & CONFIGURE SANE POOL EXPIRATION ---
        [HarmonyPatch(typeof(WorldView), "Start")]
        [HarmonyPostfix]
        public static void WorldView_Start_MemoryInit(WorldView __instance)
        {
            if (__instance == null) return;
            SharedAnimalTextureFactory = __instance.animalTextureFactory;

            if (__instance.organismObjectPool != null)
            {
                // Disable pooled organisms instead of leaving them SetActive(true) in the sky,
                // so pooled Renderers/SkinnedMeshRenderers/Animators leave Unity's active scene.
                // Expire unused pooled GameObjects after 60 days while NEVER destroying sharedMesh
                // (see OrganismObjectPool_AddFullSpecificOrganism_KeepSharedMesh_Prefix below).
                __instance.organismObjectPool.outOfSightInsteadOfDisabling = false;
                __instance.organismObjectPool.expireAfterYears = 60;
            }
        }

        // --- 1b. NEVER DESTROY SHARED MESHES ON POOL EXPIRATION ---
        // WorldView.CleanUpAnimalGameObject calls AddFullSpecificOrganism(comp, keepSharedMeshAtExpiration: false).
        // When an organism shares its Mesh with PrebuiltOrganismLibrary and other live animals (especially with
        // species-level model reuse or manually spawned animals), OrganismObjectPool.EmptyPool calls
        // Object.DestroyImmediate(component.sharedMesh, allowDestroyingAssets: true), wiping the 3D model
        // out from under live/selected animals while leaving their BoxCollider clickable.
        // Keeping sharedMesh intact lets EmptyPool destroy idle GameObjects safely while truly unreferenced
        // meshes are reclaimed by Unity's asset cleanup.
        [HarmonyPatch(typeof(OrganismObjectPool), nameof(OrganismObjectPool.AddFullSpecificOrganism))]
        [HarmonyPrefix]
        public static void OrganismObjectPool_AddFullSpecificOrganism_KeepSharedMesh_Prefix(ref bool keepSharedMeshAtExpiration)
        {
            keepSharedMeshAtExpiration = true;
        }

        // Also guard EmptyPool directly in case an organism was pooled before this prefix ran.
        [HarmonyPatch(typeof(OrganismObjectPool), nameof(OrganismObjectPool.EmptyPool), new Type[] { typeof(int), typeof(int), typeof(bool) })]
        [HarmonyPrefix]
        public static void OrganismObjectPool_EmptyPool_KeepSharedMesh_Prefix(OrganismObjectPool __instance, int definitionIndex, int sexIndex)
        {
            if (__instance != null && __instance.definitionIndicesThatShouldKeepTheirSharedMeshAtExpiration != null)
            {
                __instance.definitionIndicesThatShouldKeepTheirSharedMeshAtExpiration.Add(new DefinitionAndSexIndex(definitionIndex, sexIndex));
            }
        }

        // --- 2. SHARE TEXTURE FACTORY ACROSS BUILDERS (Eliminates duplicate GPU textures) ---
        [HarmonyPatch(typeof(AnimalObjectBuilder), nameof(AnimalObjectBuilder.Run))]
        [HarmonyPrefix]
        public static void AnimalObjectBuilder_Run_Prefix(AnimalObjectBuilder __instance)
        {
            if (__instance != null && SharedAnimalTextureFactory != null)
            {
                __instance.textureFactory = SharedAnimalTextureFactory;
            }
        }

        // --- 3. PERIODIC EXTINCT DEFINITION PRUNING ---
        [HarmonyPatch(typeof(WorldController), nameof(WorldController.ShowToday))]
        [HarmonyPostfix]
        public static void WorldController_ShowToday_MemoryGovernor(WorldController __instance)
        {
            if (__instance == null || __instance.timeLine == null) return;

            _dayCounter++;

            // Periodically clean extinct definitions from TimeLine classifiers (vanilla only does this during autosave)
            if (_dayCounter % 100 == 0)
            {
                try
                {
                    __instance.timeLine.PruneExtinctDefinitionIndices();
                }
                catch { }
            }
        }

        // --- 3b. FREE LEAKED ANIMAL HURT-FLASH MATERIALS & FIX MATERIALFACTORY LEAKS ---
        // WorldView.ShowAnimalViewsForArea allocates `new Material(Shader.Find("Standard"))`
        // every time an animal is hurt, and AnimalFlashingRed.Reset() only destroys the
        // MonoBehaviour without ever destroying redMaterial.
        [HarmonyPatch(typeof(AnimalFlashingRed), nameof(AnimalFlashingRed.Reset))]
        [HarmonyPostfix]
        public static void AnimalFlashingRed_Reset_FreeMaterial(AnimalFlashingRed __instance)
        {
            if (__instance != null && __instance.redMaterial != null)
            {
                UnityEngine.Object.Destroy(__instance.redMaterial);
                __instance.redMaterial = null;
            }
        }

        // Root fix for 169,000+ leaked Material assets in MaterialFactory.GetSkinMaterial & RecolorAnimal/RecolorPlant:
        // 1. Vanilla GetSkinMaterial never checks its cache and allocates TWO `new Material(Shader.Find("Custom/Skin"))`
        //    objects on every single call (discarding the first immediately!).
        // 2. Vanilla RecolorAnimal reads `smr.material` (which Unity clones into a leaked Material instance) for debug
        //    fields and assigns `smr.material = ...` instead of `smr.sharedMaterial = ...`.
        private struct SkinMatKey : IEquatable<SkinMatKey>
        {
            public Color Main;
            public Color Secondary;
            public int TextureId;
            public SkinPattern Pattern;

            public bool Equals(SkinMatKey other)
            {
                return Main == other.Main && Secondary == other.Secondary && TextureId == other.TextureId && Pattern == other.Pattern;
            }

            public override bool Equals(object obj)
            {
                return obj is SkinMatKey && Equals((SkinMatKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = Main.GetHashCode();
                    h = (h * 397) ^ Secondary.GetHashCode();
                    h = (h * 397) ^ TextureId;
                    h = (h * 397) ^ (int)Pattern;
                    return h;
                }
            }
        }

        private static MaterialFactory _skinCacheOwner;
        private static readonly System.Collections.Generic.Dictionary<SkinMatKey, Material> _skinMatCache =
            new System.Collections.Generic.Dictionary<SkinMatKey, Material>(128);
        private static Shader _cachedSkinShader;

        [HarmonyPatch(typeof(MaterialFactory), nameof(MaterialFactory.GetSkinMaterial))]
        [HarmonyPrefix]
        public static bool MaterialFactory_GetSkinMaterial_Prefix(
            MaterialFactory __instance,
            Color mainColor,
            Color secondaryColor,
            int textureId,
            Texture texture,
            SkinPattern pattern,
            ref Material __result)
        {
            if (__instance == null) return true;
            if (!object.ReferenceEquals(_skinCacheOwner, __instance))
            {
                _skinCacheOwner = __instance;
                _skinMatCache.Clear();
            }

            SkinMatKey key = new SkinMatKey { Main = mainColor, Secondary = secondaryColor, TextureId = textureId, Pattern = pattern };
            Material mat;
            if (_skinMatCache.TryGetValue(key, out mat) && mat != null)
            {
                __result = mat;
                return false;
            }

            if (_cachedSkinShader == null) _cachedSkinShader = Shader.Find("Custom/Skin");
            if (_cachedSkinShader == null) return true;

            mat = new Material(_cachedSkinShader);
            mat.SetColor("_MainColor", mainColor);
            mat.SetTexture("_TextureData", texture);
            mat.SetColor("_SecondaryColor", secondaryColor);
            mat.enableInstancing = true;
            if (pattern == SkinPattern.LongitudinalFading) mat.SetFloat("_CounterShadingMode", 1f);
            else if (pattern == SkinPattern.LongitudinalFadingAndBack) mat.SetFloat("_CounterShadingMode", 2f);

            _skinMatCache[key] = mat;
            __result = mat;
            return false;
        }

        [HarmonyPatch(typeof(MaterialFactory), nameof(MaterialFactory.RecolorAnimal))]
        [HarmonyPrefix]
        public static bool MaterialFactory_RecolorAnimal_Prefix(
            MaterialFactory __instance,
            RecoloringInstructions animalGameObject,
            float mainHue,
            float mainBrightness,
            float secondaryHue,
            float secondaryBrightness)
        {
            if (__instance == null || animalGameObject == null) return false;
            var renderers = animalGameObject.renderers;
            var materialTypes = animalGameObject.materialTypes;
            if (renderers == null || materialTypes == null) return false;

            Color mainColor = SimulationTools.HueAndBrightnessToColor(mainHue, mainBrightness);
            Color secColor = SimulationTools.HueAndBrightnessToColor(secondaryHue, secondaryBrightness);
            int count = Math.Min(renderers.Count, materialTypes.Count);
            for (int i = 0; i < count; i++)
            {
                SkinnedMeshRenderer smr = renderers[i];
                if (smr == null) continue;
                switch (materialTypes[i])
                {
                    case MaterialType.Main:
                        smr.sharedMaterial = __instance.GetBasicMaterial(mainColor);
                        break;
                    case MaterialType.Skin:
                        smr.sharedMaterial = __instance.GetSkinMaterial(mainColor, secColor, animalGameObject.textureID, animalGameObject.texture, animalGameObject.skinPattern);
                        break;
                    case MaterialType.Secondary:
                        smr.sharedMaterial = __instance.GetBasicMaterial(secColor);
                        break;
                    case MaterialType.Luminescent:
                        smr.sharedMaterial = __instance.GetLuminescentMaterial(mainColor, true);
                        break;
                    case MaterialType.Window:
                        smr.sharedMaterial = __instance.GetWindowMaterial(mainColor);
                        break;
                    case MaterialType.Darker:
                        smr.sharedMaterial = __instance.GetBasicMaterial(SimulationTools.HueAndBrightnessToColor(mainHue, Mathf.Clamp(mainBrightness - 0.2f, 0f, 1f)));
                        break;
                    case MaterialType.Brighter:
                        smr.sharedMaterial = __instance.GetBasicMaterial(SimulationTools.HueAndBrightnessToColor(mainHue, Mathf.Clamp(mainBrightness + 0.3f, 0f, 1f)));
                        break;
                }
            }
            return false;
        }

        [HarmonyPatch(typeof(MaterialFactory), nameof(MaterialFactory.RecolorPlant))]
        [HarmonyPrefix]
        public static bool MaterialFactory_RecolorPlant_Prefix(
            MaterialFactory __instance,
            RecoloringInstructions plantGameObject,
            float mainHue,
            float mainBrightness,
            float flowerHue,
            float flowerBrightness,
            float fruitHue,
            float fruitBrightness)
        {
            if (__instance == null || plantGameObject == null) return false;
            var renderers = plantGameObject.renderers;
            var materialTypes = plantGameObject.materialTypes;
            if (renderers == null || materialTypes == null) return false;

            Color cMain = SimulationTools.HueAndBrightnessToColor(mainHue, mainBrightness);
            Color cFlower = SimulationTools.HueAndBrightnessToColor(flowerHue, flowerBrightness);
            Color cFruit = SimulationTools.HueAndBrightnessToColor(fruitHue, fruitBrightness);
            int count = Math.Min(renderers.Count, materialTypes.Count);
            for (int i = 0; i < count; i++)
            {
                SkinnedMeshRenderer smr = renderers[i];
                if (smr == null) continue;
                switch (materialTypes[i])
                {
                    case MaterialType.Main:
                        smr.sharedMaterial = __instance.GetBasicMaterial(cMain);
                        break;
                    case MaterialType.Fruit:
                        smr.sharedMaterial = __instance.GetBasicMaterial(cFruit);
                        break;
                    case MaterialType.SpecificColor:
                        smr.sharedMaterial = __instance.GetBasicMaterial(cFlower);
                        break;
                    case MaterialType.Window:
                        smr.sharedMaterial = __instance.GetWindowMaterial(cMain);
                        break;
                    case MaterialType.Luminescent:
                        smr.sharedMaterial = __instance.GetLuminescentMaterial(cFlower, true);
                        break;
                    case MaterialType.Darker:
                        smr.sharedMaterial = __instance.GetBasicMaterial(SimulationTools.HueAndBrightnessToColor(mainHue, Mathf.Clamp(mainBrightness - 0.2f, 0f, 1f)));
                        break;
                    case MaterialType.Brighter:
                        smr.sharedMaterial = __instance.GetBasicMaterial(SimulationTools.HueAndBrightnessToColor(mainHue, Mathf.Clamp(mainBrightness + 0.3f, 0f, 1f)));
                        break;
                }
            }
            return false;
        }

        // --- 4. DEEP MEMORY TRIM (invocable via RCON) ---
        public static void TriggerDeepMemoryTrim()
        {
            if (Time.realtimeSinceStartup - _lastTrimTime < 5f) return;
            _lastTrimTime = Time.realtimeSinceStartup;

            long beforeHeap = GC.GetTotalMemory(false) / (1024 * 1024);

            try
            {
                Resources.UnloadUnusedAssets();
            }
            catch { }

            GC.Collect();

            long afterHeap = GC.GetTotalMemory(false) / (1024 * 1024);
            Plugin.LogWarn($"[MEMORY] Reclaimed RAM! Managed Heap: {beforeHeap} MB -> {afterHeap} MB.");
        }

        // --- 5. SLIM THE HISTORICAL TIMELINE WHILE LOADING A SAVE ---
        // GameSaver.Decode() rebuilds one WorldState per simulated day: a 878k-day save means
        // 878k WorldState objects. Measured from the load probe, WorldState's constructor costs
        // ~28.3 KB of committed memory per instance (39 Dictionary<int,Travel>/<int,int>, 9
        // Hashtables and 4 Lists, plus Mono's array overhead), and none of it is ever read again.
        // The running game already discards them: TimeLine.LiveADay() does
        // "line[line.Count - 2] = null" and appends the new day, so at rest only the last two
        // entries ever hold data. Skipping the historical ones keeps line.Count (and therefore
        // dayIndex and species indices) identical while removing the multi-GB allocation spike
        // that OOMs the load.
        private const long BytesPerWorldState = 28300L;
        private static long _skippedWorldStates = 0;

        [HarmonyPatch(typeof(GameSaver), "GetWorldState")]
        [HarmonyPrefix]
        public static bool GameSaver_GetWorldState_Prefix(GameSaver __instance)
        {
            // IMPORTANT: GameSaver derives from MonoBehaviour, but the game constructs it with
            // "new GameSaver()" (OverallController.cs:306, SaveGameController.cs:1900/1991/...),
            // so m_CachedPtr stays zero and Unity's overloaded == operator reports the live
            // object as null. ReferenceEquals tests the managed reference, which is what we
            // actually care about. Using "== null" here made every call take the early return,
            // so all 878k WorldStates were still built and the load still OOM'd.
            if (ReferenceEquals(__instance, null)) return true;

            int dayIndex = __instance.dayIndex;

            // Keep the last two entries, matching the steady-state shape LiveADay() leaves
            // behind: index dayIndex - 1 is TimeLine.today after the load, and dayIndex - 2 is
            // the "yesterday" that WorldController reads as line[line.Count - 2] and hands to
            // WorldView.UpdateAnimationsBetweenWorldStates, which dereferences its .areas
            // without a null check.
            if (dayIndex <= 2 || __instance.worldStates == null || __instance.worldStates.Count >= dayIndex - 2)
            {
                return true;
            }

            // Still consume the four (count, values...) blocks so the stream stays aligned.
            for (int group = 0; group < 4; group++)
            {
                int count = __instance.GetHighInteger();
                for (int i = 0; i < count; i++)
                {
                    __instance.GetHighInteger();
                }
            }

            _skippedWorldStates++;
            // Returning false without touching __result leaves the return value at its
            // default, which is null for a reference type: exactly the skipped marker.
            return false;
        }

        [HarmonyPatch(typeof(GameSaver), "Decode")]
        [HarmonyPrefix]
        public static void GameSaver_Decode_Prefix()
        {
            _skippedWorldStates = 0;
        }

        [HarmonyPatch(typeof(GameSaver), "Decode")]
        [HarmonyPostfix]
        public static void GameSaver_Decode_Postfix(GameSaver __instance)
        {
            if (_skippedWorldStates <= 0 || ReferenceEquals(__instance, null)) return;

            Plugin.LogWarn($"[MEMORY] Skipped {_skippedWorldStates} historical WorldState objects (~{_skippedWorldStates * BytesPerWorldState / 1048576L} MB) while loading day {__instance.dayIndex}.");
            _skippedWorldStates = 0;
        }

        // --- 6. SAFE CAMERA FOCUS GUARD (Eliminates NullReferenceException when clicking animals) ---
        [HarmonyPatch(typeof(WorldView), nameof(WorldView.GetNewCameraPositionForCurrentFocusedAnimal))]
        [HarmonyPrefix]
        public static bool GetNewCameraPositionForCurrentFocusedAnimal_Prefix(WorldView __instance, ref Vector3 __result)
        {
            if (__instance == null || __instance.animalViewInFocus == null || __instance.animalViewInFocus.gameObject == null || __instance.worldCamera == null)
            {
                __result = __instance != null && __instance.worldCamera != null ? __instance.worldCamera.transform.position : Vector3.zero;
                return false;
            }

            if (Vector3.Distance(__instance.cameraFocusPointOnPlane, __instance.animalViewInFocus.gameObject.transform.position) < 0.1f)
            {
                __result = __instance.worldCamera.transform.position;
                return false;
            }

            var smr = __instance.animalViewInFocus.gameObject.GetComponentInChildren<SkinnedMeshRenderer>();
            float y = (smr != null && smr.sharedMesh != null) ? smr.sharedMesh.bounds.size.y : 1.0f;

            Vector3 vector = __instance.worldScaleFactor * __instance.worldCamera.transform.position - __instance.animalViewInFocus.gameObject.transform.position;
            vector.y = 0f;
            Vector3 vector2 = __instance.worldCamera.transform.position - vector + (__instance.baseDistanceCameraToFocusedOrganism + y * __instance.animalSizeToCameraDistanceFactor) * vector.normalized;
            vector2.y = __instance.animalViewInFocus.gameObject.transform.position.y + y * __instance.animalSizeToCameraDistanceFactor;
            __result = vector2;
            return false;
        }

        [HarmonyPatch(typeof(WorldView), nameof(WorldView.GetNewCameraPositionForCurrentFocusedPlant))]
        [HarmonyPrefix]
        public static bool GetNewCameraPositionForCurrentFocusedPlant_Prefix(WorldView __instance, ref Vector3 __result)
        {
            if (__instance == null || __instance.plantViewInFocus == null || __instance.plantViewInFocus.gameObject == null || __instance.worldCamera == null)
            {
                __result = __instance != null && __instance.worldCamera != null ? __instance.worldCamera.transform.position : Vector3.zero;
                return false;
            }

            if (Vector3.Distance(__instance.cameraFocusPointOnPlane, __instance.plantViewInFocus.gameObject.transform.position) < 0.1f)
            {
                __result = __instance.worldCamera.transform.position;
                return false;
            }

            var smr = __instance.plantViewInFocus.gameObject.GetComponentInChildren<SkinnedMeshRenderer>();
            float y = 1.0f;
            if (smr != null && smr.sharedMesh != null)
            {
                y = smr.sharedMesh.bounds.size.y;
            }
            else
            {
                var mf = __instance.plantViewInFocus.gameObject.GetComponentInChildren<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) y = mf.sharedMesh.bounds.size.y;
            }

            float num = __instance.worldScaleFactor * y;
            Vector3 vector = __instance.worldCamera.transform.position - __instance.plantViewInFocus.gameObject.transform.position;
            vector.y = 0f;
            Vector3 vector2 = __instance.worldCamera.transform.position - vector + (__instance.baseDistanceCameraToFocusedOrganism + num * __instance.plantSizeToCameraDistanceFactor) * vector.normalized;
            vector2.y = __instance.plantViewInFocus.gameObject.transform.position.y + num * __instance.plantSizeToCameraDistanceFactor;
            __result = vector2;
            return false;
        }
    }
}
