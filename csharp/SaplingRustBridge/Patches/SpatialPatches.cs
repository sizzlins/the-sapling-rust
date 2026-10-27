namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Spatial management stubs. Vanilla CellDistanceFinder runs at 100% precision without
    /// distance band key collisions or dimension mismatches that broke animal food instincts.
    /// </summary>
    public static class SpatialPatches
    {
        public static class NativeGridManager
        {
            public static void Free() { }
        }
    }
}




