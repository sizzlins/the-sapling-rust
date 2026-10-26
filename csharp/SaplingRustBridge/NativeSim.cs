using System;
using System.Runtime.InteropServices;

namespace SaplingRustBridge
{
    public static class NativeSim
    {
        private const string DllName = "sapling_rust";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sapling_world_new(ulong seed);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_world_free(IntPtr world);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_world_populate(IntPtr world, uint plants, uint herbivores, float extent);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_world_tick(IntPtr world);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_world_counts(IntPtr world, out uint plants, out uint herbivores);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint sapling_world_get_plants(
            IntPtr world,
            [Out] float[] outX,
            [Out] float[] outY,
            [Out] float[] outScale,
            [Out] float[] outEnergy,
            uint maxLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint sapling_world_get_herbivores(
            IntPtr world,
            [Out] float[] outX,
            [Out] float[] outY,
            [Out] float[] outEnergy,
            uint maxLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint sapling_find_nearest_plants_batch(
            IntPtr world,
            [In] float[] queryX,
            [In] float[] queryY,
            float maxDist,
            [Out] int[] outIndices,
            uint count);

        [StructLayout(LayoutKind.Sequential)]
        public struct WarpStats
        {
            public uint PlantMutations;
            public uint AnimalMutations;
            public uint PlantBirths;
            public uint AnimalBirths;
            public uint PlantDeaths;
            public uint AnimalDeaths;
            public uint ActivePlants;
            public uint ActiveHerbivores;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint sapling_world_warp(IntPtr world, uint years, ref WarpStats outStats);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint sapling_timeline_push(
            IntPtr world,
            uint day,
            uint totalPlants,
            uint totalAnimals,
            [In] ushort[] speciesCounts,
            uint speciesLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint sapling_timeline_len(IntPtr world);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint sapling_timeline_get(
            IntPtr world,
            uint day,
            [Out] ushort[] outCounts,
            uint maxLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_world_rebuild_grid(IntPtr world);


        [StructLayout(LayoutKind.Sequential)]
        public struct CreatureDecision
        {
            public uint Action;
            public float TargetX;
            public float TargetY;
            public int TargetIdx;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint sapling_creatures_evaluate_batch(
            IntPtr world,
            [In] float[] xs,
            [In] float[] ys,
            [In] float[] energies,
            [In] byte[] isCarnivore,
            uint count,
            float maxSearch,
            [Out] CreatureDecision[] outDecisions);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint sapling_compute_billboards_batch(
            IntPtr world,
            float camX,
            float camY,
            float camZ,
            float maxDist,
            [Out] UnityEngine.Matrix4x4[] outMatrices,
            uint maxLen);

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeInstinctQuery
        {
            public int CellIndex;
            public int MinDistance;
            public int MaxDistance;
            public int ConditionSense;
            public int ConditionObject;
            public byte Top;
            public byte Left;
            public byte Right;
            public byte Bottom;
            public int OwnSpeciesIndex;
            public int CursorCellIndex;
            public float HueMin;
            public float HueMax;
            public float BrightnessMin;
            public float BrightnessMax;
            public float AddHueMin;
            public float AddHueMax;
            public uint CanEatFlags;
            public float CanEatAtHeight;
            public byte CanFly;
            public byte CanClimb;
            public byte ReadyInternalReceiver;
            public byte ReadyInternalGiver;
            public byte ReadyExternal;
            public byte InvertHueRange;
            public ushort SkinPatternMask;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sapling_grid_create(uint width, uint height);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_grid_free(IntPtr grid);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_grid_clear_organisms(IntPtr grid);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_grid_set_terrain(IntPtr grid, uint idx, float oceanDepth, byte isWater);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_grid_set_plant(
            IntPtr grid,
            uint idx,
            short species,
            float hue,
            float brightness,
            float leafHeight,
            byte fruitRipe,
            float fruitHue,
            byte barkNone);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_grid_set_alga(
            IntPtr grid,
            uint idx,
            byte isPlankton,
            float hue,
            float brightness);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_grid_set_grass(
            IntPtr grid,
            uint idx,
            float hue,
            float brightness);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_grid_set_animal(
            IntPtr grid,
            uint idx,
            short species,
            float hue,
            float brightness,
            byte skinPattern,
            byte fertFlags,
            float stepLoudness,
            byte isGlowing);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_grid_set_carrion(IntPtr grid, uint idx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sapling_grid_search_instinct(IntPtr grid, ref NativeInstinctQuery query);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint sapling_grid_search_instincts_batch(
            IntPtr grid,
            [In] NativeInstinctQuery[] queries,
            uint count,
            [Out] int[] outResults);

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeDaySummary
        {
            public uint PlantBirths;
            public uint PlantDeaths;
            public uint AnimalBirths;
            public uint AnimalDeaths;
            public uint FungusBirths;
            public uint FungusDeaths;
            public uint AlgaBirths;
            public uint AlgaDeaths;
            public uint TotalPlants;
            public uint TotalAnimals;
            public uint TotalFungi;
            public uint TotalAlgae;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_grid_set_cell_env(
            IntPtr grid,
            uint idx,
            float temp,
            float shallowW,
            float deepW,
            float nutrients,
            ushort detritus,
            float oceanDepth,
            float steepness,
            float wind);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_grid_set_fungus(
            IntPtr grid,
            uint idx,
            short species,
            byte isParasite,
            float energy,
            ushort cycleDuration);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_grid_set_plant_bio(
            IntPtr grid,
            uint idx,
            float health,
            float energy,
            ushort age,
            ushort maxAge,
            byte seeds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sapling_grid_set_animal_bio(
            IntPtr grid,
            uint idx,
            float health,
            float energy,
            float water,
            ushort age,
            ushort maxAge,
            byte diet);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sapling_grid_step_day(
            IntPtr grid,
            float mutAlgae,
            float mutPlants,
            float mutAnimals,
            float mutFungi,
            ulong seed,
            ref NativeDaySummary outSummary);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ushort sapling_grid_get_detritus(IntPtr grid, uint idx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern float sapling_grid_get_nutrients(IntPtr grid, uint idx);
    }
}

