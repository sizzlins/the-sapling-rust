namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// UNWIRED (kept for reference). Spatial management stubs. Vanilla CellDistanceFinder runs
    /// at 100% precision without the distance band key collisions or dimension mismatches that
    /// broke animal food instincts when the native grid was tried. Plugin no longer registers
    /// this class and no longer calls NativeGridManager.Free().
    /// </summary>
    public static class SpatialPatches
    {
        public static class NativeGridManager
        {
            public static void Free() { }
        }
    }
}




