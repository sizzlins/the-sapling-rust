using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace TheSaplingVanillaLauncher
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string targetExe = Path.Combine(baseDir, "the_sapling.exe");

                if (!File.Exists(targetExe))
                {
                    string fallback = @"D:\The_Sapling\The Sapling\the_sapling.exe";
                    if (File.Exists(fallback))
                    {
                        targetExe = fallback;
                        baseDir = Path.GetDirectoryName(fallback);
                    }
                    else
                    {
                        MessageBox.Show(
                            "Could not find 'the_sapling.exe'.\nPlease place this launcher in the game directory:\nD:\\The_Sapling\\The Sapling\\",
                            "The Sapling (Vanilla)",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return;
                    }
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = targetExe;
                psi.WorkingDirectory = baseDir;
                psi.UseShellExecute = false;

                if (args != null && args.Length > 0)
                {
                    psi.Arguments = string.Join(" ", args);
                }

                // Completely disable Doorstop / BepInEx / Harmony mods for this game session
                psi.EnvironmentVariables["DOORSTOP_DISABLE"] = "TRUE";

                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Error launching The Sapling in vanilla mode:\n\n" + ex.Message,
                    "The Sapling (Vanilla)",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
