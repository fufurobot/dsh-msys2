// Msys2Flavour — the MSYS2 environments this installer can build presets for,
// and the discovery of the msys2_shell.cmd they all run through.
//
// Shared by the WinForms installer and the test harness, so the two cannot
// drift: the tests exercise exactly the code the installer runs.

using System;
using System.Collections.Generic;
using System.IO;

namespace Dsh.Msys2Installer
{
    /// <summary>One MSYS2 environment, and the preset built for it.</summary>
    public sealed class Msys2Flavour
    {
        /// <summary>The msys2_shell.cmd flag, without the leading dash.</summary>
        public readonly string Key;

        /// <summary>The MSYSTEM value msys2_shell.cmd exports for this environment.</summary>
        public readonly string Msystem;

        /// <summary>Human-facing name, shown in the picker.</summary>
        public readonly string Label;

        /// <summary>One-line description, shown under the name.</summary>
        public readonly string Blurb;

        public Msys2Flavour(string key, string msystem, string label, string blurb)
        {
            Key = key;
            Msystem = msystem;
            Label = label;
            Blurb = blurb;
        }

        /// <summary>Preset directory name; also the id the DSH roster shows.</summary>
        public string PresetId { get { return "msys2-" + Key; } }

        /// <summary>The msys2_shell.cmd flag, e.g. "-mingw64".</summary>
        public string ShellFlag { get { return "-" + Key; } }
    }

    /// <summary>The five environments, in picker order.</summary>
    public static class Msys2Flavours
    {
        // The bare MSYS environment is deliberately absent: the request was for
        // the four MinGW toolchains plus clangarm64, and bare MSYS is a build
        // environment rather than a target toolchain.
        public static readonly Msys2Flavour[] All = new Msys2Flavour[]
        {
            new Msys2Flavour("mingw64", "MINGW64",
                "MSYS2 / MinGW64",
                "x86_64 GCC toolchain; the default MSYS2 environment."),
            new Msys2Flavour("mingw32", "MINGW32",
                "MSYS2 / MinGW32",
                "i686 GCC toolchain for 32-bit targets."),
            new Msys2Flavour("ucrt64", "UCRT64",
                "MSYS2 / UCRT64",
                "x86_64 GCC against the Universal C Runtime."),
            new Msys2Flavour("clang64", "CLANG64",
                "MSYS2 / CLANG64",
                "x86_64 LLVM/Clang toolchain with UCRT."),
            new Msys2Flavour("clangarm64", "CLANGARM64",
                "MSYS2 / CLANGARM64",
                "AArch64 LLVM/Clang toolchain (Windows on ARM)."),
        };
    }

    /// <summary>Finding and validating msys2_shell.cmd.</summary>
    public static class Msys2Discovery
    {
        public const string ShellCmdName = "msys2_shell.cmd";

        /// <summary>
        /// Whether a path is a usable msys2_shell.cmd: the batch file itself,
        /// with usr\bin\bash.exe beside it. The sibling check is what
        /// distinguishes a real MSYS2 root from a stray copy.
        /// </summary>
        public static bool IsShellCmd(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                string root = Path.GetDirectoryName(Path.GetFullPath(path));
                return File.Exists(Path.Combine(root, "usr", "bin", "bash.exe"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// msys2_shell.cmd from PATH, or null.
        ///
        /// PATH entries are searched by hand rather than through a helper
        /// because each must be validated for its sibling tree, and a PATH
        /// entry may be quoted.
        /// </summary>
        public static string FindOnPath()
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;

            foreach (string raw in path.Split(Path.PathSeparator))
            {
                string entry = raw.Trim().Trim('"');
                if (entry.Length == 0) continue;

                string candidate;
                try { candidate = Path.Combine(entry, ShellCmdName); }
                catch (Exception) { continue; } // An invalid PATH entry, not our problem.

                if (IsShellCmd(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>
        /// Common MSYS2 install locations, most likely first. Used to pre-fill
        /// the file dialog so the usual case is two clicks. Nothing here is
        /// trusted without validation.
        /// </summary>
        public static List<string> WellKnownLocations()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] roots = new string[]
            {
                Path.Combine(home, "Downloads"),
                home,
                "C:\\",
                "C:\\msys64",
                "C:\\tools",
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (string root in roots)
            {
                string candidate;
                try { candidate = Path.Combine(root, "msys64", ShellCmdName); }
                catch (Exception) { continue; }
                if (seen.Add(candidate)) result.Add(candidate);

                try { candidate = Path.Combine(root, ShellCmdName); }
                catch (Exception) { continue; }
                if (seen.Add(candidate)) result.Add(candidate);
            }
            return result;
        }

        /// <summary>Best-effort discovery: PATH first, then the well-known roots.</summary>
        public static string Guess()
        {
            string found = FindOnPath();
            if (found != null) return found;

            foreach (string candidate in WellKnownLocations())
                if (IsShellCmd(candidate)) return candidate;

            return null;
        }

        /// <summary>
        /// Validate a user-supplied path. Returns null when it is usable, or a
        /// human-readable reason when it is not, so the GUI can show either.
        /// </summary>
        public static string Validate(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "No file selected.";

            if (!File.Exists(path))
                return "Not a file: " + path;

            if (!string.Equals(Path.GetFileName(path), ShellCmdName, StringComparison.OrdinalIgnoreCase))
                return "Expected a file named " + ShellCmdName + ", got \"" + Path.GetFileName(path)
                     + "\". Select the msys2_shell.cmd at the top of your MSYS2 install root.";

            string root = Path.GetDirectoryName(Path.GetFullPath(path));
            string bash = Path.Combine(root, "usr", "bin", "bash.exe");
            if (!File.Exists(bash))
                return "Found " + ShellCmdName + " but not " + bash + " beside it. "
                     + "Select the msys2_shell.cmd at the top of a full MSYS2 install.";

            return null;
        }
    }

    /// <summary>Where the installer writes.</summary>
    public static class DshPaths
    {
        /// <summary>The harness home: %USERPROFILE%\.dsh, or $DSH_HOME when set.</summary>
        public static string DshHome()
        {
            string over = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrEmpty(over)) return over;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }

        /// <summary>
        /// Where locally authored presets live. This mirrors DSH's own
        /// `dsh-agent-presets`, which scans `<dshHome>/.agent-presets`.
        /// </summary>
        public static string PresetRoot()
        {
            return Path.Combine(DshHome(), ".agent-presets");
        }
    }
}
