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
        /// The configuration file that names this machine's MSYS2 install.
        ///
        /// It exists because auto-discovery cannot be right everywhere: a
        /// developer may keep several MSYS2 trees, and CI runners keep one in a
        /// place no developer ever uses. The file is read from the process's
        /// working directory and is git-ignored, so a per-machine choice never
        /// becomes a repository-wide one.
        /// </summary>
        public const string EnvFileName = ".env";

        /// <summary>
        /// Keys accepted in .env (and as real environment variables) naming the
        /// directory that holds msys2_shell.cmd. DSH_MSYS2_ROOT is checked
        /// first; MSYS2_ROOT is the name a human is most likely to guess.
        /// </summary>
        public static readonly string[] RootKeys = new string[]
        {
            "DSH_MSYS2_ROOT",
            "MSYS2_ROOT",
            "DSH_MSYS2_HOME",
        };

        /// <summary>
        /// Where a GitHub Actions checkout of this repository lives, or null.
        ///
        /// GITHUB_WORKSPACE is set by the runner. LOCALAPPDATA is what allows
        /// this same branch to be exercised in a test, which is the only way
        /// the CI layout can be covered without being on CI.
        ///
        /// When neither yields a path, the working directory itself is walked
        /// upwards looking for the repository marker. That fallback is what
        /// keeps the "a .env below the checkout root is ignored" rule from
        /// quietly doing nothing in an ordinary shell, where no runner
        /// variables exist at all.
        /// </summary>
        public static string CheckoutDirectory()
        {
            string workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
            if (!string.IsNullOrEmpty(workspace))
            {
                try { return Path.GetFullPath(workspace); }
                catch (Exception) { return null; }
            }

            return LocalCheckoutDirectory() ?? DiscoverCheckoutFromWorkingDirectory();
        }

        /// <summary>
        /// This machine's own GitHub Actions checkout, if it has one:
        /// %LOCALAPPDATA%\GitHubActions\...\work\<repo>\<repo>. Requiring a
        /// "work" directory keeps a stray LOCALAPPDATA value from inventing a
        /// checkout that does not exist.
        /// </summary>
        private static string LocalCheckoutDirectory()
        {
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(local)) return null;

            try
            {
                string root = Path.Combine(local, "GitHubActions");
                if (!Directory.Exists(root)) return null;

                var found = new List<string>();
                foreach (string work in Directory.GetDirectories(root, "work", SearchOption.AllDirectories))
                    foreach (string repo in Directory.GetDirectories(work))
                    {
                        string candidate = Path.Combine(repo, Path.GetFileName(repo));
                        if (Directory.Exists(candidate)) found.Add(Path.GetFullPath(candidate));
                    }
                if (found.Count == 0) return null;

                found.Sort(StringComparer.OrdinalIgnoreCase);
                return found[0];
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Walk up from the working directory to the nearest directory that
        /// looks like this repository's root, or null.
        /// </summary>
        private static string DiscoverCheckoutFromWorkingDirectory()
        {
            string dir;
            try { dir = Path.GetFullPath(Directory.GetCurrentDirectory()); }
            catch (Exception) { return null; }

            while (!string.IsNullOrEmpty(dir))
            {
                if (IsRepositoryRoot(dir))
                {
                    try { return Path.GetFullPath(dir); }
                    catch (Exception) { return null; }
                }

                DirectoryInfo parent;
                try { parent = Directory.GetParent(dir); }
                catch (Exception) { return null; }
                if (parent == null) return null;
                dir = parent.FullName;
            }
            return null;
        }

        /// <summary>
        /// Whether a directory looks like the root of this repository. Only
        /// files that are part of the tree are used, so this is decided by the
        /// checkout and not by anything local to a machine.
        /// </summary>
        private static bool IsRepositoryRoot(string dir)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "build.cmd"))) return true;

                // A bare directory listing with no source files is not a root,
                // so require the source tree alongside the marker.
                if (Directory.Exists(Path.Combine(dir, "src"))
                    && File.Exists(Path.Combine(dir, "src", "Msys2Flavour.cs"))) return true;
            }
            catch (Exception)
            {
                // An unreadable directory is simply not the root.
            }
            return false;
        }

        /// <summary>
        /// Whether a path is inside the checkout this process is running from.
        /// This is what stops a repository-local .env from overriding the
        /// deliberately configured .env at the repository root.
        /// </summary>
        private static bool IsUnderCheckout(string path)
        {
            string checkout = CheckoutDirectory();
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(checkout)) return false;

            try
            {
                string full = Path.GetFullPath(path);
                string root = checkout.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The roots GitHub Actions puts MSYS2 in, most specific first.
        ///
        /// setup-msys2 installs to $RUNNER_TOOL_CACHE\msys2\msys64 and exports
        /// MSYS2_LOCATION, but neither its PATH entry nor its output is visible
        /// to a process that the runner did not start. That is the case here:
        /// ci.yml compiles the shim with csc.exe and then runs the test suite as
        /// a plain child process, which inherits the runner's environment but
        /// not the action's in-step PATH edits. So the layout is reconstructed.
        ///
        /// The action's `location:` input is what puts the tree somewhere else;
        /// ci.yml sets it to `${{ github.workspace }}\msys`, giving
        /// <workspace>\msys\msys64 — and ${{ github.workspace }} is literally
        /// the GITHUB_WORKSPACE variable, so the layout can be derived here
        /// rather than hard-coded to a runner path.
        /// </summary>
        public static List<string> GitHubWorkspaceRoots()
        {
            var roots = new List<string>();
            string workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
            if (string.IsNullOrEmpty(workspace)) return roots;

            try
            {
                roots.Add(Path.Combine(workspace, "msys", "msys64"));

                // What the action would produce with its default location,
                // which is $RUNNER_TOOL_CACHE. Included so this keeps working
                // if ci.yml ever drops the explicit `location:`.
                string cache = Environment.GetEnvironmentVariable("RUNNER_TOOL_CACHE");
                if (!string.IsNullOrEmpty(cache))
                    roots.Add(Path.Combine(cache, "msys2", "msys64"));
            }
            catch (Exception)
            {
                // An unusable GITHUB_WORKSPACE is not worth failing over.
            }
            return roots;
        }

        /// <summary>
        /// Every .env worth reading, in precedence order: the current
        /// directory, then each directory above it, then the checkout root.
        ///
        /// Ancestors are walked because the test suite runs with the repository
        /// root as its working directory while a developer is more likely to be
        /// sitting in src/ or build/. A .env found inside the checkout (i.e.
        /// below the checkout root) is deliberately not read: a file that ships
        /// with the sources must not decide where MSYS2 is.
        /// </summary>
        public static List<string> ConfigFileCandidates()
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string checkout = CheckoutDirectory();
            string dir;
            try { dir = Path.GetFullPath(Directory.GetCurrentDirectory()); }
            catch (Exception) { dir = null; }

            while (!string.IsNullOrEmpty(dir))
            {
                // Inside the checkout every directory but the root is skipped,
                // so the walk continues upward without reading anything.
                if (checkout == null || !IsUnderCheckout(dir) || PathsMatch(dir, checkout))
                {
                    string file = Path.Combine(dir, EnvFileName);
                    if (seen.Add(file)) candidates.Add(file);
                }

                DirectoryInfo parent;
                try { parent = Directory.GetParent(dir); }
                catch (Exception) { break; }
                if (parent == null) break;
                dir = parent.FullName;
            }

            if (checkout != null)
            {
                string file = Path.Combine(checkout, EnvFileName);
                if (seen.Add(file)) candidates.Add(file);
            }
            return candidates;
        }

        private static bool PathsMatch(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// A tiny dotenv reader for the handful of keys in RootKeys.
        ///
        /// It handles the subset the file is documented to support: `KEY=value`
        /// and `KEY: value`, `#` comments, blank lines, an optional `export`
        /// prefix, CRLF endings, and quoted values (single quotes literal,
        /// double quotes understanding the usual backslash escapes). It
        /// deliberately does NOT do variable interpolation: a $ in a Windows
        /// path or a password must not be silently expanded.
        ///
        /// Malformed lines are skipped rather than thrown on. A parse failure
        /// must not become a failed install when PATH may still find MSYS2.
        /// </summary>
        public static Dictionary<string, string> ParseEnvFile(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(path)) return values;

            string[] lines;
            try
            {
                if (!File.Exists(path)) return values;
                lines = File.ReadAllLines(path);
            }
            catch (Exception)
            {
                return values;
            }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                // `export FOO=bar` is common enough in hand-written files to be
                // worth accepting, even though nothing here is a shell.
                if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                    line = line.Substring("export ".Length).TrimStart();

                string key, value;
                int equals = line.IndexOf('=');
                int colon = line.IndexOf(':');

                if (equals >= 0 && (colon < 0 || equals < colon))
                {
                    key = line.Substring(0, equals);
                    value = line.Substring(equals + 1);
                }
                else if (colon > 0)
                {
                    // YAML-ish `KEY: value`. A Windows path starts with a drive
                    // letter, so a colon in first position is not a separator.
                    key = line.Substring(0, colon);
                    value = line.Substring(colon + 1);
                }
                else
                {
                    continue;
                }

                key = key.Trim();
                if (key.Length == 0) continue;
                values[key] = Unquote(value.Trim());
            }
            return values;
        }

        /// <summary>Strip matching surrounding quotes and undouble any escapes.</summary>
        private static string Unquote(string value)
        {
            if (value.Length >= 2)
            {
                if (value[0] == '\'' && value[value.Length - 1] == '\'')
                    return value.Substring(1, value.Length - 2); // Single quotes are literal.

                if (value[0] == '"' && value[value.Length - 1] == '"')
                {
                    string inner = value.Substring(1, value.Length - 2);
                    return inner.Replace("\\\\", "\u0000")   // Protect an escaped backslash…
                                .Replace("\\\"", "\"")
                                .Replace("\\n", "\n")
                                .Replace("\\t", "\t")
                                .Replace("\u0000", "\\");    // …then restore it verbatim.
                }
            }
            return value;
        }

        /// <summary>
        /// First value among RootKeys in an already-parsed .env.
        ///
        /// MSYS2_ROOT and DSH_MSYS2_HOME are consulted even when
        /// DSH_MSYS2_ROOT is present, so that explicitly clearing the preferred
        /// key (which is how a caller says "ignore this one") falls through to
        /// the next rather than disabling the file.
        /// </summary>
        private static string RootFrom(Dictionary<string, string> values)
        {
            foreach (string key in RootKeys)
            {
                string value;
                if (values.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
                    return value;
            }
            return null;
        }

        /// <summary>
        /// The directory named by a real environment variable, or null. An
        /// explicitly empty variable means "not configured here", so the lookup
        /// moves on instead of stopping.
        /// </summary>
        public static string RootFromEnvironment()
        {
            foreach (string key in RootKeys)
            {
                string value = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return null;
        }

        /// <summary>
        /// The first .env that both exists and names a usable root, or null.
        /// </summary>
        public static string ConfiguredRoot()
        {
            foreach (string candidate in ConfigFileCandidates())
            {
                string root = RootFrom(ParseEnvFile(candidate));
                if (!string.IsNullOrEmpty(root)) return root;
            }
            return null;
        }

        /// <summary>
        /// Whether a path is a usable msys2_shell.cmd: the batch file itself,
        /// with usr\bin\bash.exe beside it. The sibling check is what
        /// distinguishes a real MSYS2 root from a stray copy.
        /// </summary>
        public static bool IsShellCmd(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

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
        /// msys2_shell.cmd inside one directory, or null.
        ///
        /// This is the shared tail of every lookup that starts from a bare
        /// directory: PATH entries, the roots named by GITHUB_WORKSPACE, and
        /// the roots read out of .env. Callers are responsible for handling a
        /// malformed path themselves, because only they know whether that is
        /// worth reporting.
        /// </summary>
        public static string FindInDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return null;

            // A directory name is routinely written with a trailing separator
            // ("C:\tools\"). Path.Combine tolerates that, but normalising here
            // means every discovered path is reported in one shape.
            string clean = directory.Trim().Trim('"').TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (clean.Length == 0) return null;

            string candidate;
            try { candidate = Path.Combine(clean, ShellCmdName); }
            catch (Exception) { return null; } // Not a valid path, so not a candidate.

            return IsShellCmd(candidate) ? candidate : null;
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
        /// the file dialog so the usual case is two clicks, and as the last
        /// resort of Guess(). Nothing here is trusted without validation.
        /// </summary>
        public static List<string> WellKnownLocations()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots = new List<string>
            {
                Path.Combine(home, "Downloads"),
                home,
                "C:\\",
                "C:\\msys64",
                "C:\\tools",
            };

            // The GitHub Actions layout leads when a runner has one, because
            // there the usual developer locations are definitively wrong.
            roots.InsertRange(0, GitHubWorkspaceRoots());

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;

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

        /// <summary>
        /// Best-effort discovery, most explicit source first:
        ///
        ///   1. the DSH_MSYS2_ROOT / MSYS2_ROOT environment variable
        ///   2. a git-ignored .env in this directory or an ancestor
        ///   3. the GitHub Actions layout under GITHUB_WORKSPACE
        ///   4. msys2_shell.cmd on PATH
        ///   5. the well-known install locations
        ///
        /// Every source is validated; the first one that yields a real
        /// msys2_shell.cmd with usr\bin\bash.exe beside it wins.
        /// </summary>
        public static string Guess()
        {
            string found = ExplicitRoot();
            if (found != null) return found;

            found = FindOnPath();
            if (found != null) return found;

            foreach (string candidate in WellKnownLocations())
                if (IsShellCmd(candidate)) return candidate;

            return null;
        }

        /// <summary>
        /// The install named by configuration rather than by the machine: the
        /// root environment variable, then .env. Returns null when neither
        /// names anything usable, so Guess() can fall back to PATH.
        /// </summary>
        public static string ExplicitRoot()
        {
            string shellCmd = FindUnder(RootFromEnvironment());
            if (shellCmd != null) return shellCmd;

            return FindUnder(ConfiguredRoot());
        }

        /// <summary>
        /// Whether discovery is being driven by explicit configuration rather
        /// than by PATH. The test suite uses this to separate a real
        /// regression from a machine that simply has MSYS2 somewhere else.
        /// </summary>
        public static bool HasExplicitConfiguration()
        {
            return RootFromEnvironment() != null
                || !string.IsNullOrEmpty(ConfiguredRoot());
        }

        /// <summary>
        /// Resolve a configured directory to msys2_shell.cmd. A generous
        /// reading of "the MSYS2 location" is accepted, because a user writing
        /// a path by hand will plausibly give any of these:
        ///
        ///   C:\msys64                  the install root
        ///   C:\msys64\msys2_shell.cmd  the launcher itself
        ///   C:\msys64\                trailing separator
        ///   "C:\Program Files\msys64" quotes left in place
        /// </summary>
        public static string FindUnder(string configured)
        {
            if (string.IsNullOrEmpty(configured)) return null;

            string path = configured.Trim().Trim('"');

            if (path.EndsWith(ShellCmdName, StringComparison.OrdinalIgnoreCase))
            {
                // The launcher was named directly. A spare copy on PATH is
                // often a stub, so the real install is preferred when the
                // named file's own directory does not stand on its own.
                if (IsShellCmd(path)) return path;

                string near = FindInDirectory(Path.GetDirectoryName(path));
                if (near != null) return near;
                return FindOnPath();
            }

            return FindInDirectory(path);
        }

        /// <summary>
        /// A failure message naming every source that was tried, because "not
        /// found" with no explanation is the hardest version of this bug to
        /// diagnose on a machine you cannot log into.
        /// </summary>
        public static string DescribeFailure()
        {
            var lines = new List<string>();
            lines.Add("No MSYS2 installation was found.");

            lines.Add("Looked in " + EnvFileName + " and the environment variable "
                + string.Join(" / ", RootKeys) + "; neither named a usable "
                + ShellCmdName + ".");

            foreach (string candidate in ConfigFileCandidates())
                lines.Add("  " + (File.Exists(candidate) ? "read    " : "missing ") + candidate);

            List<string> workspaceRoots = GitHubWorkspaceRoots();
            if (workspaceRoots.Count > 0)
            {
                lines.Add("Looked in the GitHub Actions layout:");
                foreach (string root in workspaceRoots)
                    lines.Add("  " + (IsShellCmd(Path.Combine(root, ShellCmdName)) ? "found   " : "missing ")
                        + Path.Combine(root, ShellCmdName));
            }

            lines.Add("Looked on PATH: "
                + (FindOnPath() ?? "no " + ShellCmdName + " in any PATH entry"));

            string explicitRoot = ExplicitRoot();
            if (explicitRoot != null) lines.Add("Configured install: " + explicitRoot);

            lines.Add("To fix this, create a " + EnvFileName + " beside the repository root (it is git-ignored) with:");
            lines.Add("  DSH_MSYS2_ROOT=C:\\msys64");
            return string.Join(Environment.NewLine, lines.ToArray());
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

    /// <summary>Where the installer reads its configuration from.</summary>
    public static class DshConfig
    {
        /// <summary>
        /// Record the chosen msys2_shell.cmd as DSH_MSYS2_ROOT for this process
        /// and for its children.
        ///
        /// The test suite hands the same install to several helpers by starting
        /// child processes, and the shim and the installer both rediscover
        /// MSYS2; publishing the validated choice once means none of them has
        /// to rediscover it — or disagree about it.
        /// </summary>
        public static void PublishMsys2Root(string shellCmd)
        {
            if (string.IsNullOrEmpty(shellCmd)) return;

            try
            {
                Environment.SetEnvironmentVariable(
                    RootKey,
                    Path.GetDirectoryName(Path.GetFullPath(shellCmd)));
            }
            catch (Exception)
            {
                // An unusable path is not worth failing an otherwise valid run.
            }
        }

        /// <summary>Preferred key naming the MSYS2 root; see RootKeys.</summary>
        public const string RootKey = "DSH_MSYS2_ROOT";

        /// <summary>
        /// Find the .env to edit, preferring an existing one and otherwise the
        /// nearest directory that is not inside the checkout. Returns null when
        /// there is nowhere sensible to write.
        /// </summary>
        public static string ConfigFileForWriting()
        {
            foreach (string candidate in Msys2Discovery.ConfigFileCandidates())
                if (File.Exists(candidate)) return candidate;

            List<string> candidates = Msys2Discovery.ConfigFileCandidates();
            return candidates.Count > 0 ? candidates[0] : null;
        }

        /// <summary>
        /// Write DSH_MSYS2_ROOT into the .env, creating it when absent.
        ///
        /// This is what makes the installer's Browse button stick: a path the
        /// user picked once is rediscovered on every later run without the
        /// dialog. Existing keys are updated in place and everything else in
        /// the file is preserved, because the file belongs to the user.
        /// </summary>
        public static string SaveMsys2Root(string shellCmd)
        {
            return WriteRootTo(ConfigFileForWriting(), shellCmd);
        }

        /// <summary>
        /// Save an install to one specific .env. Split out from
        /// SaveMsys2Root so the round-trip can be tested without the test
        /// having to stand in whichever directory discovery would pick.
        /// Returns the path written, or null when it could not be written.
        /// </summary>
        public static string WriteRootTo(string path, string shellCmd)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(shellCmd)) return null;

            string root;
            try { root = Path.GetDirectoryName(Path.GetFullPath(shellCmd)); }
            catch (Exception) { return null; }
            if (string.IsNullOrEmpty(root)) return null;

            string line = RootKey + "=" + root;

            var lines = new List<string>();
            if (File.Exists(path))
            {
                string[] existing;
                try { existing = File.ReadAllLines(path); }
                catch (Exception) { existing = new string[0]; }

                var written = false;
                foreach (string raw in existing)
                {
                    string key = KeyOf(raw);
                    if (key == null || !IsRootKey(key)) lines.Add(raw);
                    else if (!written) { lines.Add(line); written = true; }
                    // A duplicate key from another tool is dropped rather than
                    // left to win a later parse; the last write is ours.
                }
                if (!written) lines.Add(line);
            }
            else
            {
                lines.Add("# Machine-local MSYS2 configuration for the DSH MSYS2 installer.");
                lines.Add("# Git-ignored: this is a per-machine choice, not a repository one.");
                lines.Add(line);
            }

            try
            {
                File.WriteAllLines(path, lines.ToArray());
                PublishMsys2Root(shellCmd);
                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsRootKey(string key)
        {
            foreach (string known in Msys2Discovery.RootKeys)
                if (string.Equals(known, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The KEY of a `KEY=value` or `KEY: value` line, or null.</summary>
        private static string KeyOf(string raw)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') return null;
            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                line = line.Substring("export ".Length).TrimStart();

            int equals = line.IndexOf('=');
            int colon = line.IndexOf(':');
            int cut = equals >= 0 && (colon < 0 || equals < colon) ? equals
                    : colon > 0 ? colon
                    : -1;
            if (cut <= 0) return null;

            string key = line.Substring(0, cut).Trim();
            return key.Length == 0 ? null : key;
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
