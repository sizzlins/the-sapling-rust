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
    }
}
