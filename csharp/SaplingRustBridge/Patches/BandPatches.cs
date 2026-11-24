using System;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace SaplingRustBridge.Patches
{
    /// <summary>
    /// Routes retail's distance-band queries to the native implementation that
    /// the parity harness proves byte-identical (tools/parity-harness).
    ///
    /// Patched at <see cref="CellDistanceFinder"/> rather than at WorldArea so
    /// every caller goes through it: the 6-argument band query is the single
    /// allocation-heavy spatial primitive in the plant day loop.
    ///
    /// Safety rules:
    ///  - any failure falls back to the retail method, never to a guess;
    ///  - the returned array is always freshly allocated, because callers keep
    ///    the reference across nested queries (e.g. the pollen loop holds the
    ///    outer band while Fertilize runs). A shared scratch buffer would
    ///    silently corrupt the outer loop, so only the scratch buffer used to
    ///    receive native output is shared, never the result.
    ///  - distance outside retail's switch (1..20) yields an empty array, which
    ///    is what retail itself returns.
    /// </summary>
    [HarmonyPatch]
    public static class BandPatches
    {
        private const int FLAG_TOP = 1;
        private const int FLAG_LEFT = 2;
        private const int FLAG_RIGHT = 4;
        private const int FLAG_BOTTOM = 8;

        /// <summary>Largest retail band is 16 blocks * 8 = 128 ints.</summary>
        private const int Capacity = 256;

        // Exact retail offset table (d = 1..20), matching src/band.rs and verified
        // byte-identical against sapling_rust.dll at startup (943,552-case oracle).
        private static readonly byte[][][] ManagedBand = BuildBandTable();
        public static bool ManagedBandReady;
        private static bool _managedVerified;

        private static byte[][][] BuildBandTable()
        {
            return new byte[21][][]
            {
                new byte[0][],
                new[] { new byte[] {0,1,0} },
                new[] { new byte[] {0,2,0}, new byte[] {1,1,1} },
                new[] { new byte[] {0,3,0}, new byte[] {1,2,1}, new byte[] {2,2,2} },
                new[] { new byte[] {0,4,0}, new byte[] {1,3,1}, new byte[] {2,3,2} },
                new[] { new byte[] {0,5,0}, new byte[] {1,4,1}, new byte[] {2,4,2}, new byte[] {3,3,3}, new byte[] {3,4,4} },
                new[] { new byte[] {0,6,0}, new byte[] {1,5,1}, new byte[] {2,5,2}, new byte[] {3,5,3}, new byte[] {4,4,4} },
                new[] { new byte[] {0,7,0}, new byte[] {1,6,1}, new byte[] {2,6,2}, new byte[] {3,6,3}, new byte[] {4,5,4} },
                new[] { new byte[] {0,8,0}, new byte[] {1,7,1}, new byte[] {2,7,2}, new byte[] {3,7,3}, new byte[] {4,6,4}, new byte[] {5,5,5}, new byte[] {5,6,6} },
                new[] { new byte[] {0,9,0}, new byte[] {1,8,1}, new byte[] {2,8,2}, new byte[] {3,8,3}, new byte[] {4,7,4}, new byte[] {4,8,5}, new byte[] {5,7,6}, new byte[] {6,6,7} },
                new[] { new byte[] {0,10,0}, new byte[] {1,9,1}, new byte[] {2,9,2}, new byte[] {3,9,3}, new byte[] {4,9,4}, new byte[] {5,8,5}, new byte[] {6,7,6}, new byte[] {6,8,7}, new byte[] {7,7,8} },
                new[] { new byte[] {0,11,0}, new byte[] {1,10,1}, new byte[] {2,10,2}, new byte[] {3,10,3}, new byte[] {4,10,4}, new byte[] {5,9,5}, new byte[] {6,9,6}, new byte[] {7,8,7} },
                new[] { new byte[] {0,12,0}, new byte[] {1,11,1}, new byte[] {2,11,2}, new byte[] {3,11,3}, new byte[] {4,11,4}, new byte[] {5,10,5}, new byte[] {6,10,6}, new byte[] {7,9,7}, new byte[] {8,8,8} },
                new[] { new byte[] {0,13,0}, new byte[] {1,12,1}, new byte[] {2,12,2}, new byte[] {3,12,3}, new byte[] {4,12,4}, new byte[] {5,11,5}, new byte[] {5,12,6}, new byte[] {6,11,7}, new byte[] {7,10,8}, new byte[] {8,9,9}, new byte[] {8,10,10}, new byte[] {9,9,11} },
                new[] { new byte[] {0,14,0}, new byte[] {1,14,1}, new byte[] {2,14,2}, new byte[] {3,14,3}, new byte[] {4,14,4}, new byte[] {5,14,5}, new byte[] {6,13,6}, new byte[] {7,13,7}, new byte[] {8,12,8}, new byte[] {9,11,9}, new byte[] {10,10,10}, new byte[] {10,11,11} },
                new[] { new byte[] {0,15,0}, new byte[] {1,15,1}, new byte[] {2,15,2}, new byte[] {3,15,3}, new byte[] {4,15,4}, new byte[] {5,15,5}, new byte[] {6,14,6}, new byte[] {7,14,7}, new byte[] {8,13,8}, new byte[] {9,12,9}, new byte[] {9,13,10}, new byte[] {10,12,11}, new byte[] {11,11,12} },
                new[] { new byte[] {0,16,0}, new byte[] {1,16,1}, new byte[] {2,16,2}, new byte[] {3,16,3}, new byte[] {4,16,4}, new byte[] {5,16,5}, new byte[] {6,15,6}, new byte[] {7,15,7}, new byte[] {8,14,8}, new byte[] {9,14,9}, new byte[] {10,13,10}, new byte[] {11,12,11}, new byte[] {12,12,12} },
                new[] { new byte[] {0,17,0}, new byte[] {1,17,1}, new byte[] {2,17,2}, new byte[] {3,17,3}, new byte[] {4,17,4}, new byte[] {5,17,5}, new byte[] {6,16,6}, new byte[] {7,16,7}, new byte[] {8,15,8}, new byte[] {8,16,9}, new byte[] {9,15,10}, new byte[] {10,14,11}, new byte[] {11,13,12}, new byte[] {11,14,13}, new byte[] {12,13,14} },
                new[] { new byte[] {0,18,0}, new byte[] {1,18,1}, new byte[] {2,18,2}, new byte[] {3,18,3}, new byte[] {4,18,4}, new byte[] {5,18,5}, new byte[] {6,17,6}, new byte[] {6,18,7}, new byte[] {7,17,8}, new byte[] {8,17,9}, new byte[] {9,16,10}, new byte[] {10,15,11}, new byte[] {10,16,12}, new byte[] {11,15,13}, new byte[] {12,14,14}, new byte[] {13,13,15} },
                new[] { new byte[] {0,19,0}, new byte[] {1,19,1}, new byte[] {2,19,2}, new byte[] {3,19,3}, new byte[] {4,19,4}, new byte[] {5,19,5}, new byte[] {6,19,6}, new byte[] {7,18,7}, new byte[] {8,18,8}, new byte[] {9,17,9}, new byte[] {10,17,10}, new byte[] {11,16,11}, new byte[] {12,15,12}, new byte[] {13,14,13}, new byte[] {13,15,14}, new byte[] {14,14,15} },
                new[] { new byte[] {0,20,0}, new byte[] {1,20,1}, new byte[] {2,20,2}, new byte[] {3,20,3}, new byte[] {4,20,4}, new byte[] {5,20,5}, new byte[] {6,20,6}, new byte[] {7,19,7}, new byte[] {8,19,8}, new byte[] {9,18,9}, new byte[] {10,18,10}, new byte[] {11,17,11}, new byte[] {12,16,12}, new byte[] {12,17,13}, new byte[] {13,16,14}, new byte[] {14,15,15} },
            };
        }

        /// <summary>
        /// Zero-allocation, zero-P/Invoke band fill matching src/band.rs slot for slot.
        /// Verified at startup against sapling_rust.dll before ManagedBandReady becomes true.
        /// </summary>
        public static int FillBandDirect(float sizeX, float sizeY, int center, int distance, byte mask, int[] outBuf)
        {
            if (distance <= 0 || distance > 20) return 0;
            byte[][] pairs = ManagedBand[distance];
            int needed = pairs.Length * 8;
            if (outBuf == null || outBuf.Length < needed) return -needed;

            int sizeYInt = (int)sizeY;
            if (sizeYInt <= 0 || sizeX <= 0f) return 0;

            int centerX = (int)Math.Floor((double)((float)center / sizeY));
            int centerY = center % sizeYInt;

            bool top = (mask & FLAG_TOP) != 0;
            bool left = (mask & FLAG_LEFT) != 0;
            bool right = (mask & FLAG_RIGHT) != 0;
            bool bottom = (mask & FLAG_BOTTOM) != 0;

            for (int i = 0; i < pairs.Length; i++)
            {
                byte[] p = pairs[i];
                int x = p[0];
                int y = p[1];
                int b = p[2] * 8;
                bool diagonal = (x == y);

                outBuf[b] = (((float)(centerX + x) < sizeX) && ((float)(centerY + y) < sizeY) && (top || (right && diagonal)))
                    ? ((centerX + x) * sizeYInt + (centerY + y)) : -1;

                outBuf[b + 1] = ((centerX - x >= 0) && (centerY - y >= 0) && (bottom || (left && diagonal)))
                    ? ((centerX - x) * sizeYInt + (centerY - y)) : -1;

                if (x != 0 && y != 0)
                {
                    outBuf[b + 2] = (((float)(centerX + x) < sizeX) && (centerY - y >= 0) && (bottom || (right && diagonal)))
                        ? ((centerX + x) * sizeYInt + (centerY - y)) : -1;
                    outBuf[b + 3] = ((centerX - x >= 0) && ((float)(centerY + y) < sizeY) && (top || (left && diagonal)))
                        ? ((centerX - x) * sizeYInt + (centerY + y)) : -1;
                }
                else
                {
                    outBuf[b + 2] = -1;
                    outBuf[b + 3] = -1;
                }

                if (x != y)
                {
                    outBuf[b + 4] = (((float)(centerX + y) < sizeX) && ((float)(centerY + x) < sizeY) && right)
                        ? ((centerX + y) * sizeYInt + (centerY + x)) : -1;
                    outBuf[b + 5] = ((centerX - y >= 0) && (centerY - x >= 0) && left)
                        ? ((centerX - y) * sizeYInt + (centerY - x)) : -1;

                    if (x != 0 && y != 0)
                    {
                        outBuf[b + 6] = (((float)(centerX + y) < sizeX) && (centerY - x >= 0) && right)
                            ? ((centerX + y) * sizeYInt + (centerY - x)) : -1;
                        outBuf[b + 7] = ((centerX - y >= 0) && ((float)(centerY + x) < sizeY) && left)
                            ? ((centerX - y) * sizeYInt + (centerY + x)) : -1;
                    }
                    else
                    {
                        outBuf[b + 6] = -1;
                        outBuf[b + 7] = -1;
                    }
                }
                else
                {
                    outBuf[b + 4] = -1;
                    outBuf[b + 5] = -1;
                    outBuf[b + 6] = -1;
                    outBuf[b + 7] = -1;
                }
            }

            return needed;
        }

        /// <summary>
        /// Compact zero-allocation band fill emitting ONLY valid in-bounds cell indices
        /// (omitting all -1 padding) in the exact same order as FillBandDirect.
        /// Verified at startup against sapling_rust.dll over all 1,080 matrix checks.
        /// </summary>
        public static int FillBandCompact(int sizeXInt, int sizeYInt, int centerX, int centerY, int distance, byte mask, int[] outBuf)
        {
            if (distance <= 0 || distance > 20) return 0;
            byte[][] pairs = ManagedBand[distance];
            bool top = (mask & FLAG_TOP) != 0;
            bool left = (mask & FLAG_LEFT) != 0;
            bool right = (mask & FLAG_RIGHT) != 0;
            bool bottom = (mask & FLAG_BOTTOM) != 0;

            int w = 0;
            for (int i = 0; i < pairs.Length; i++)
            {
                byte[] p = pairs[i];
                int x = p[0];
                int y = p[1];
                bool diagonal = (x == y);

                int cxPx = centerX + x;
                int cxMx = centerX - x;
                int cyPy = centerY + y;
                int cyMy = centerY - y;

                if (cxPx < sizeXInt && cyPy < sizeYInt && (top || (right && diagonal)))
                {
                    outBuf[w++] = cxPx * sizeYInt + cyPy;
                }
                if (cxMx >= 0 && cyMy >= 0 && (bottom || (left && diagonal)))
                {
                    outBuf[w++] = cxMx * sizeYInt + cyMy;
                }
                if (x != 0 && y != 0)
                {
                    if (cxPx < sizeXInt && cyMy >= 0 && (bottom || (right && diagonal)))
                    {
                        outBuf[w++] = cxPx * sizeYInt + cyMy;
                    }
                    if (cxMx >= 0 && cyPy < sizeYInt && (top || (left && diagonal)))
                    {
                        outBuf[w++] = cxMx * sizeYInt + cyPy;
                    }
                }
                if (!diagonal)
                {
                    int cxPy = centerX + y;
                    int cxMy = centerX - y;
                    int cyPx = centerY + x;
                    int cyMx = centerY - x;

                    if (right && cxPy < sizeXInt && cyPx < sizeYInt)
                    {
                        outBuf[w++] = cxPy * sizeYInt + cyPx;
                    }
                    if (left && cxMy >= 0 && cyMx >= 0)
                    {
                        outBuf[w++] = cxMy * sizeYInt + cyMx;
                    }
                    if (x != 0 && y != 0)
                    {
                        if (right && cxPy < sizeXInt && cyMx >= 0)
                        {
                            outBuf[w++] = cxPy * sizeYInt + cyMx;
                        }
                        if (left && cxMy >= 0 && cyPx < sizeYInt)
                        {
                            outBuf[w++] = cxMy * sizeYInt + cyPx;
                        }
                    }
                }
            }

            return w;
        }

        /// <summary>
        /// Cross-checks FillBandDirect and FillBandCompact against sapling_rust.dll
        /// (the 943k-case verified oracle) across all distances 1..20, edge/corner/interior
        /// cells, and direction masks.
        /// </summary>
        public static void VerifyManagedAgainstNative()
        {
            if (_managedVerified) return;
            _managedVerified = true;

            try
            {
                IntPtr nativeBuf = Marshal.AllocHGlobal(Capacity * sizeof(int));
                try
                {
                    int[] nativeArr = new int[Capacity];
                    int[] managedArr = new int[Capacity];
                    int[] compactArr = new int[Capacity];
                    int[] centers = { 0, 1, 63, 64, 32 * 64 + 32, 63 * 64 + 63 };
                    byte[] masks = { 0x0F, 0x01, 0x02, 0x04, 0x08, 0x05, 0x0A, 0x07, 0x0D };

                    for (int d = 1; d <= 20; d++)
                    {
                        for (int c = 0; c < centers.Length; c++)
                        {
                            int center = centers[c];
                            int cx = center / 64;
                            int cy = center % 64;
                            for (int m = 0; m < masks.Length; m++)
                            {
                                int nw = NativeBandFill(64f, 64f, center, d, masks[m], nativeBuf, Capacity);
                                int mw = FillBandDirect(64f, 64f, center, d, masks[m], managedArr);
                                int cw = FillBandCompact(64, 64, cx, cy, d, masks[m], compactArr);
                                if (nw != mw || nw < 0)
                                {
                                    Plugin.LogWarn("[BAND] managed self-check length mismatch at d=" + d);
                                    ManagedBandReady = false;
                                    return;
                                }
                                if (nw > 0)
                                {
                                    Marshal.Copy(nativeBuf, nativeArr, 0, nw);
                                    int ci = 0;
                                    for (int i = 0; i < nw; i++)
                                    {
                                        if (nativeArr[i] != managedArr[i])
                                        {
                                            Plugin.LogWarn("[BAND] managed self-check value mismatch at d=" + d + " slot=" + i);
                                            ManagedBandReady = false;
                                            return;
                                        }
                                        if (nativeArr[i] != -1)
                                        {
                                            if (ci >= cw || compactArr[ci] != nativeArr[i])
                                            {
                                                Plugin.LogWarn("[BAND] compact self-check mismatch at d=" + d + " slot=" + ci);
                                                ManagedBandReady = false;
                                                return;
                                            }
                                            ci++;
                                        }
                                    }
                                    if (ci != cw)
                                    {
                                        Plugin.LogWarn("[BAND] compact self-check count mismatch at d=" + d);
                                        ManagedBandReady = false;
                                        return;
                                    }
                                }
                            }
                        }
                    }

                    ManagedBandReady = true;
                    Plugin.LogInfo("[BAND] zero-alloc direct+compact band verified byte-identical to sapling_rust.dll (1,080 matrix checks).");
                }
                finally
                {
                    Marshal.FreeHGlobal(nativeBuf);
                }
            }
            catch (Exception ex)
            {
                ManagedBandReady = false;
                Plugin.LogWarn("[BAND] managed self-check skipped (" + ex.GetType().Name + "); falling back to P/Invoke/retail.");
            }
        }

        /// <summary>Set from config before patching.</summary>
        public static bool Enabled = true;

        /// <summary>
        /// Shadow mode: retail runs, and every native result is compared against
        /// it on live game data. This is the in-game version of the parity
        /// harness, so a blind spot in the offline size matrix cannot hide.
        /// After <see cref="ShadowVerifyCalls"/> clean comparisons it flips to
        /// native automatically; any single mismatch pins it to retail forever.
        /// </summary>
        public static bool ShadowMode = true;
        public static long ShadowVerifyCalls = 20000;
        public static long ShadowChecked;
        public static long ShadowMismatches;

        public static long Calls;
        public static long Fallbacks;
        public static long Empty;

        private static bool _unavailable;
        private static bool _loggedFirstCall;
        private static bool _loggedFirstFallback;

        private static readonly int[] EmptyArray = new int[0];

        [ThreadStatic]
        private static IntPtr _scratch;

        [ThreadStatic]
        private static int[] _verify;

        public static void Cleanup()
        {
            if (_scratch != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_scratch);
                _scratch = IntPtr.Zero;
            }
        }

        [DllImport("sapling_rust", EntryPoint = "sapling_band_fill", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NativeBandFill(
            float sizeX, float sizeY, int centerIndex, int distance, byte mask, IntPtr outBuf, int capacity);

        [HarmonyPatch(typeof(CellDistanceFinder), "GetAllCellsWithinDistanceBandOfCellWithIndex",
            new[] { typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
        [HarmonyPrefix]
        public static bool Prefix(
            CellDistanceFinder __instance,
            int centerIndex,
            int distance,
            bool top,
            bool left,
            bool right,
            bool bottom,
            ref int[] __result)
        {
            if (!Enabled || _unavailable || __instance == null)
            {
                return true;
            }

            // Verification phase: let retail compute the real answer, then the
            // postfix compares ours against it.
            if (ShadowMode)
            {
                return true;
            }

            // Fast-path empty distances (0 or > 20) without crossing P/Invoke:
            // measured at ~9.7M calls per session.
            if (distance <= 0 || distance > 20)
            {
                __result = EmptyArray;
                Empty++;
                return false;
            }

            // Retail divides by (int)size.y and strides by it; a degenerate
            // world is not worth reasoning about, so let retail own it.
            if ((int)__instance.size.y <= 0 || __instance.size.x <= 0f)
            {
                return true;
            }

            byte mask = 0;
            if (top) { mask |= FLAG_TOP; }
            if (left) { mask |= FLAG_LEFT; }
            if (right) { mask |= FLAG_RIGHT; }
            if (bottom) { mask |= FLAG_BOTTOM; }

            // Direct zero-P/Invoke path once verified against sapling_rust.dll.
            if (ManagedBandReady)
            {
                int needed = ManagedBand[distance].Length * 8;
                var directResult = new int[needed];
                int w = FillBandDirect(__instance.size.x, __instance.size.y, centerIndex, distance, mask, directResult);
                if (w == needed)
                {
                    __result = directResult;
                    Calls++;
                    if (!_loggedFirstCall)
                    {
                        _loggedFirstCall = true;
                        Plugin.LogInfo("BandPatches: direct band active (first call d=" + distance + ", " + w + " ints)");
                    }
                    return false;
                }
            }

            try
            {
                if (_scratch == IntPtr.Zero)
                {
                    _scratch = Marshal.AllocHGlobal(Capacity * sizeof(int));
                }

                int written = NativeBandFill(
                    __instance.size.x, __instance.size.y, centerIndex, distance, mask, _scratch, Capacity);

                if (written < 0)
                {
                    // Buffer too small or native refused: retail decides.
                    Fallbacks++;
                    if (!_loggedFirstFallback)
                    {
                        _loggedFirstFallback = true;
                        Plugin.LogWarn("BandPatches: native returned " + written + "; falling back to retail");
                    }
                    return true;
                }

                if (written == 0)
                {
                    __result = EmptyArray;
                    Empty++;
                    return false;
                }

                var result = new int[written];
                Marshal.Copy(_scratch, result, 0, written);
                __result = result;

                Calls++;
                if (!_loggedFirstCall)
                {
                    _loggedFirstCall = true;
                    Plugin.LogInfo("BandPatches: native band active (first call d=" + distance + ", " + written + " ints)");
                }
                return false;
            }
            catch (DllNotFoundException)
            {
                Disable("sapling_rust.dll not found");
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                Disable("export sapling_band_fill missing (stale dll?)");
                return true;
            }
            catch (Exception ex)
            {
                Disable(ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        /// <summary>
        /// Shadow verification: retail has already computed the real answer, so
        /// compare ours against it on live game data. Any mismatch pins the
        /// plugin to retail permanently, because a wrong band silently breaks
        /// animal food instincts - the failure that killed the previous attempt.
        /// </summary>
        [HarmonyPatch(typeof(CellDistanceFinder), "GetAllCellsWithinDistanceBandOfCellWithIndex",
            new[] { typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
        [HarmonyPostfix]
        public static void Postfix(
            CellDistanceFinder __instance,
            int centerIndex,
            int distance,
            bool top,
            bool left,
            bool right,
            bool bottom,
            int[] __result)
        {
            if (!Enabled || !ShadowMode || _unavailable || __instance == null)
            {
                return;
            }
            if ((int)__instance.size.y <= 0 || __instance.size.x <= 0f)
            {
                return;
            }
            if (ShadowChecked >= ShadowVerifyCalls)
            {
                ShadowMode = false;
                Plugin.LogWarn("[BAND] verified against retail on " + ShadowChecked + " live calls; switching to native");
                return;
            }

            try
            {
                if (_scratch == IntPtr.Zero)
                {
                    _scratch = Marshal.AllocHGlobal(Capacity * sizeof(int));
                }

                byte mask = 0;
                if (top) { mask |= FLAG_TOP; }
                if (left) { mask |= FLAG_LEFT; }
                if (right) { mask |= FLAG_RIGHT; }
                if (bottom) { mask |= FLAG_BOTTOM; }

                int written = NativeBandFill(
                    __instance.size.x, __instance.size.y, centerIndex, distance, mask, _scratch, Capacity);
                ShadowChecked++;

                bool mismatch = written < 0 || __result == null || written != __result.Length;
                int at = -1;
                if (!mismatch && written > 0)
                {
                    if (_verify == null)
                    {
                        _verify = new int[Capacity];
                    }
                    Marshal.Copy(_scratch, _verify, 0, written);
                    for (int i = 0; i < written; i++)
                    {
                        if (_verify[i] != __result[i])
                        {
                            mismatch = true;
                            at = i;
                            break;
                        }
                    }
                }

                if (mismatch)
                {
                    ShadowMismatches++;
                    Plugin.LogWarn("[BAND] MISMATCH size=(" + __instance.size.x + "," + __instance.size.y
                        + ") centre=" + centerIndex + " d=" + distance + " mask=" + mask
                        + " retailLen=" + (__result == null ? -1 : __result.Length) + " nativeLen=" + written
                        + (at >= 0 ? " slot=" + at + " retail=" + __result[at] + " native=" + _verify[at] : string.Empty));
                    Disable("live mismatch vs retail");
                }
            }
            catch (Exception ex)
            {
                Disable("shadow verify failed: " + ex.GetType().Name);
            }
        }

        private static void Disable(string reason)
        {
            if (_unavailable)
            {
                return;
            }
            _unavailable = true;
            Plugin.LogWarn("BandPatches: disabled, using retail band (" + reason + ")");
        }

        /// <summary>One-line counter summary, for the profiler window / RCON status.</summary>
        public static string Status()
        {
            if (!Enabled) { return "band=native(disabled)"; }
            if (_unavailable) { return "band=retail(fallback)"; }
            return "band=native calls=" + Calls + " empty=" + Empty + " fallbacks=" + Fallbacks;
        }
    }
}
