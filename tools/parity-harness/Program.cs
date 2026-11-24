using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ParityHarness
{
    /// <summary>
    /// Differential harness: retail <c>CellDistanceFinder</c> (the oracle) versus
    /// the native <c>sapling_band_fill</c> export, compared int-for-int.
    ///
    /// Modes:
    ///   dump                 fingerprint the oracle alone (no native needed)
    ///   compare &lt;dll&gt;        exact diff oracle vs native
    ///   mutation             how far a "reasonable" reimplementation drifts
    /// </summary>
    internal static class Program
    {
        private const int FLAG_TOP = 1;
        private const int FLAG_LEFT = 2;
        private const int FLAG_RIGHT = 4;
        private const int FLAG_BOTTOM = 8;

        private static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "dump";

            if (mode == "dump")
            {
                return Dump();
            }
            if (mode == "compare")
            {
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("usage: ParityHarness compare <path-to-sapling_rust.dll>");
                    return 2;
                }
                return Compare(args[1]);
            }
            if (mode == "mutation")
            {
                return Mutation();
            }

            Console.Error.WriteLine("unknown mode: " + mode);
            return 2;
        }

        // ---------------------------------------------------------------- cases

        private struct Case
        {
            public float Sx;
            public float Sy;
            public int Center;
            public int Distance;
            public byte Mask;
        }

        private static readonly float[,] Sizes =
        {
            { 64f, 64f },      // square, the common planet size
            { 80f, 50f },      // wide, non-square
            { 47f, 31f },      // odd, prime-ish
            { 3f, 3f },        // smaller than every band radius
            { 1f, 1f },        // degenerate
            { 63.5f, 40.25f }, // fractional: float bounds vs truncated stride
        };

        /// <summary>
        /// Cell indices worth probing: every cell on small worlds, border plus a
        /// stride-5 interior sample on large ones (holes show up at the edges),
        /// plus indices outside the grid to pin the guard behaviour.
        /// </summary>
        private static List<int> Centers(float sx, float sy)
        {
            int w = (int)sx;
            int h = (int)sy;
            if (w <= 0 || h <= 0)
            {
                return new List<int> { 0 };
            }

            var list = new List<int>();
            if (w * h <= 2500)
            {
                for (int i = 0; i < w * h; i++)
                {
                    list.Add(i);
                }
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    for (int y = 0; y < h; y++)
                    {
                        bool border = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                        if (border || (x % 5 == 0 && y % 5 == 0))
                        {
                            list.Add(x * h + y);
                        }
                    }
                }
            }

            list.Add(-1);
            list.Add(-7);
            list.Add(w * h);
            list.Add(w * h + 123);
            return list;
        }

        private static IEnumerable<Case> Cases()
        {
            for (int s = 0; s < Sizes.GetLength(0); s++)
            {
                float sx = Sizes[s, 0];
                float sy = Sizes[s, 1];
                List<int> centers = Centers(sx, sy);
                for (int c = 0; c < centers.Count; c++)
                {
                    for (int d = 0; d <= 22; d++)
                    {
                        for (int m = 0; m < 16; m++)
                        {
                            yield return new Case
                            {
                                Sx = sx,
                                Sy = sy,
                                Center = centers[c],
                                Distance = d,
                                Mask = (byte)m,
                            };
                        }
                    }
                }
            }
        }

        private static CellDistanceFinder NewOracle(float sx, float sy)
        {
            var finder = new CellDistanceFinder();
            finder.Initialize(new Vector2(sx, sy));
            return finder;
        }

        private static int[] OracleBand(CellDistanceFinder finder, Case c)
        {
            return finder.GetAllCellsWithinDistanceBandOfCellWithIndex(
                c.Center,
                c.Distance,
                (c.Mask & FLAG_TOP) != 0,
                (c.Mask & FLAG_LEFT) != 0,
                (c.Mask & FLAG_RIGHT) != 0,
                (c.Mask & FLAG_BOTTOM) != 0);
        }

        // ----------------------------------------------------------------- dump

        private static int Dump()
        {
            ulong hash = 1469598103934665603UL;
            long cases = 0;
            long ints = 0;

            string currentSize = null;
            CellDistanceFinder finder = null;

            foreach (Case c in Cases())
            {
                string key = c.Sx + "x" + c.Sy;
                if (!string.Equals(key, currentSize, StringComparison.Ordinal))
                {
                    currentSize = key;
                    finder = NewOracle(c.Sx, c.Sy);
                }

                int[] band = OracleBand(finder, c);
                cases++;
                ints += band.Length;

                hash = Mix(hash, BitConverter.GetBytes(c.Sx));
                hash = Mix(hash, BitConverter.GetBytes(c.Sy));
                hash = Mix(hash, BitConverter.GetBytes(c.Center));
                hash = Mix(hash, BitConverter.GetBytes(c.Distance));
                hash = Mix(hash, new[] { c.Mask });
                hash = Mix(hash, BitConverter.GetBytes(band.Length));
                for (int i = 0; i < band.Length; i++)
                {
                    hash = Mix(hash, BitConverter.GetBytes(band[i]));
                }
            }

            Console.WriteLine("mode           dump (oracle only)");
            Console.WriteLine("cases          " + cases);
            Console.WriteLine("ints           " + ints);
            Console.WriteLine("fnv1a64        " + hash.ToString("x16"));

            // A handful of eyeball cases, so the shape is visible, not just a hash.
            CellDistanceFinder f = NewOracle(8f, 8f);
            foreach (int d in new[] { 1, 2, 5 })
            {
                int[] mid = f.GetAllCellsWithinDistanceBandOfCellWithIndex(
                    3 * 8 + 3, d, true, true, true, true);
                Console.WriteLine("sample d=" + d + " centre(3,3) all-flags: " + Render(mid));

                int[] corner = f.GetAllCellsWithinDistanceBandOfCellWithIndex(
                    0, d, true, true, true, true);
                Console.WriteLine("sample d=" + d + " corner(0,0) all-flags: " + Render(corner));
            }
            int[] gate = f.GetAllCellsWithinDistanceBandOfCellWithIndex(3 * 8 + 3, 1, true, true, false, false);
            Console.WriteLine("sample d=1 centre(3,3) top+left only:  " + Render(gate));

            return 0;
        }

        private static string Render(int[] a)
        {
            if (a.Length == 0)
            {
                return "[]";
            }
            return "[" + string.Join(",", a) + "]";
        }

        // -------------------------------------------------------------- compare

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32")]
        private static extern bool FreeLibrary(IntPtr module);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int BandFillFn(float sx, float sy, int center, int dist, byte mask, IntPtr outBuf, int cap);

        private static int Compare(string dllPath)
        {
            IntPtr module = LoadLibraryW(dllPath);
            if (module == IntPtr.Zero)
            {
                Console.Error.WriteLine("LoadLibrary failed (" + Marshal.GetLastWin32Error() + "): " + dllPath);
                return 2;
            }
            IntPtr proc = GetProcAddress(module, "sapling_band_fill");
            if (proc == IntPtr.Zero)
            {
                Console.Error.WriteLine("export sapling_band_fill not found in " + dllPath);
                FreeLibrary(module);
                return 2;
            }

            var native = (BandFillFn)Marshal.GetDelegateForFunctionPointer(proc, typeof(BandFillFn));

            const int Cap = 256;
            IntPtr buffer = Marshal.AllocHGlobal(Cap * sizeof(int));
            var scratch = new int[Cap];

            long cases = 0;
            long mismatches = 0;
            long intsCompared = 0;
            int shown = 0;

            try
            {
                string currentSize = null;
                CellDistanceFinder finder = null;

                foreach (Case c in Cases())
                {
                    string key = c.Sx + "x" + c.Sy;
                    if (!string.Equals(key, currentSize, StringComparison.Ordinal))
                    {
                        currentSize = key;
                        finder = NewOracle(c.Sx, c.Sy);
                    }

                    int[] expected = OracleBand(finder, c);
                    int written = native(c.Sx, c.Sy, c.Center, c.Distance, c.Mask, buffer, Cap);
                    cases++;

                    // Contract: negative return means "required length" for a short buffer.
                    int required = written < 0 ? -written : written;
                    if (written < 0)
                    {
                        mismatches++;
                        if (shown++ < 10)
                        {
                            Console.WriteLine("MISMATCH short-buffer contract: " + Describe(c) + " returned " + written);
                        }
                        continue;
                    }

                    if (required != expected.Length)
                    {
                        mismatches++;
                        if (shown++ < 10)
                        {
                            Console.WriteLine("MISMATCH length: " + Describe(c)
                                + " expected " + expected.Length + " got " + required);
                        }
                        continue;
                    }

                    Marshal.Copy(buffer, scratch, 0, required);

                    bool bad = false;
                    int badAt = -1;
                    for (int i = 0; i < required; i++)
                    {
                        intsCompared++;
                        if (scratch[i] != expected[i])
                        {
                            bad = true;
                            badAt = i;
                            break;
                        }
                    }
                    if (bad)
                    {
                        mismatches++;
                        if (shown++ < 10)
                        {
                            Console.WriteLine("MISMATCH value: " + Describe(c) + " at slot " + badAt
                                + " expected " + expected[badAt] + " got " + scratch[badAt]);
                            Console.WriteLine("   expected " + Render(expected));
                            Console.WriteLine("   actual   " + Render(Trim(scratch, required)));
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                FreeLibrary(module);
            }

            Console.WriteLine("mode           compare");
            Console.WriteLine("native         " + dllPath);
            Console.WriteLine("cases          " + cases);
            Console.WriteLine("ints compared  " + intsCompared);
            Console.WriteLine("mismatches     " + mismatches);
            Console.WriteLine(mismatches == 0 ? "RESULT         PASS - byte-identical to retail" : "RESULT         FAIL");
            return mismatches == 0 ? 0 : 1;
        }

        private static int[] Trim(int[] a, int n)
        {
            var r = new int[n];
            Array.Copy(a, r, n);
            return r;
        }

        private static string Describe(Case c)
        {
            return "size(" + c.Sx + "," + c.Sy + ") centre " + c.Center
                + " d " + c.Distance + " mask " + c.Mask;
        }

        /// <summary>
        /// Mutation probe. A "reasonable" reimplementation returns the cells
        /// within a radius instead of retail's mirrored half-quadrant band.
        /// If the harness said PASS against this, the harness would be worthless.
        /// </summary>
        private static int Mutation()
        {
            long cases = 0;
            long differing = 0;
            int shown = 0;

            string currentSize = null;
            CellDistanceFinder finder = null;
            List<int> naive = new List<int>();

            foreach (Case c in Cases())
            {
                string key = c.Sx + "x" + c.Sy;
                if (!string.Equals(key, currentSize, StringComparison.Ordinal))
                {
                    currentSize = key;
                    finder = NewOracle(c.Sx, c.Sy);
                }

                int[] expected = OracleBand(finder, c);
                cases++;

                NaiveDisc(naive, c);
                bool same = naive.Count == expected.Length;
                if (same)
                {
                    for (int i = 0; i < naive.Count; i++)
                    {
                        if (naive[i] != expected[i])
                        {
                            same = false;
                            break;
                        }
                    }
                }

                if (!same)
                {
                    differing++;
                    if (shown++ < 5)
                    {
                        Console.WriteLine("differs: " + Describe(c));
                        Console.WriteLine("   retail " + Render(expected));
                        Console.WriteLine("   naive  " + Render(naive.ToArray()));
                    }
                }
            }

            Console.WriteLine("mode           mutation (retail vs a plausible reimplementation)");
            Console.WriteLine("cases          " + cases);
            Console.WriteLine("differing      " + differing
                + " (" + (100.0 * differing / Math.Max(1, cases)).ToString("F1") + "%)");
            Console.WriteLine(differing > 0
                ? "RESULT         the harness discriminates: a neat/derived band is NOT retail"
                : "RESULT         harness is blind - investigate before trusting it");
            return differing > 0 ? 0 : 1;
        }

        private static void NaiveDisc(List<int> into, Case c)
        {
            into.Clear();
            int w = (int)c.Sx;
            int h = (int)c.Sy;
            if (w <= 0 || h <= 0 || c.Distance <= 0)
            {
                return;
            }
            int cx = (int)Math.Floor(c.Center / c.Sy);
            int cy = c.Center % h;
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    int dx = x - cx;
                    int dy = y - cy;
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }
                    if (dx * dx + dy * dy <= c.Distance * c.Distance)
                    {
                        into.Add(x * h + y);
                    }
                }
            }
        }

        // --------------------------------------------------------------- hashing

        private static ulong Mix(ulong hash, byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }
}
