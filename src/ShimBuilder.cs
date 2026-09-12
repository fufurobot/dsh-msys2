// ShimBuilder — compiles the shell shim from its C# source at install time.
//
// The shim is shipped as source (Msys2ShellShim.cs) and compiled on the target
// machine, rather than shipping a prebuilt .exe. That keeps this repository
// free of binaries and means the shim always matches the source the user can
// read.
//
// The compiler is the .NET Framework csc.exe, which ships with Windows itself
// — so the installer needs no SDK and no network access.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Dsh.Msys2Installer
{
    /// <summary>Compilation failed, carrying the compiler's own output.</summary>
    public sealed class ShimBuildException : Exception
    {
        public ShimBuildException(string message) : base(message) { }
    }

    public static class ShimBuilder
    {
        public const string SourceFileName = "Msys2ShellShim.cs";

        /// <summary>
        /// The shim source, looked up beside this assembly first and then in a
        /// source-tree layout. The second case is what makes running the
        /// installer straight out of the repository work without an install
        /// step of its own.
        /// </summary>
        public static string SourcePath()
        {
            string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SourceFileName);
            if (File.Exists(beside)) return beside;

            // Running from src\ or the project root: look in a few likely spots.
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int depth = 0; depth < 4 && dir != null; depth++)
            {
                string candidate = Path.Combine(dir, SourceFileName);
                if (File.Exists(candidate)) return candidate;

                string nested = Path.Combine(dir, "src", SourceFileName);
                if (File.Exists(nested)) return nested;

                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return beside; // Report the expected location in the error.
        }

        /// <summary>
        /// Find a usable C# compiler.
        ///
        /// Preference order matters: the .NET Framework csc.exe ships with
        /// Windows, so it exists even where no SDK is installed, and it emits a
        /// plain IL assembly that runs on the framework every Windows has.
        /// </summary>
        public static string FindCompiler()
        {
            string windows = Environment.GetEnvironmentVariable("SystemRoot");
            if (string.IsNullOrEmpty(windows)) windows = "C:\\Windows";

            string[] frameworks = new string[] { "Framework64", "Framework" };
            foreach (string name in frameworks)
            {
                string frameworkDir = Path.Combine(windows, "Microsoft.NET", name);
                if (!Directory.Exists(frameworkDir)) continue;

                // Newest framework version first.
                var versions = new List<string>(Directory.GetDirectories(frameworkDir));
                versions.Sort();
                versions.Reverse();

                foreach (string version in versions)
                {
                    string csc = Path.Combine(version, "csc.exe");
                    if (File.Exists(csc)) return csc;
                }
            }

            // Last resort: a csc on PATH (a Visual Studio developer prompt).
            string path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string entry in path.Split(Path.PathSeparator))
                {
                    string trimmed = entry.Trim().Trim('"');
                    if (trimmed.Length == 0) continue;
                    try
                    {
                        string candidate = Path.Combine(trimmed, "csc.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch (Exception) { }
                }
            }
            return null;
        }

        /// <summary>
        /// Compile the shim source to <paramref name="target"/>, returning the
        /// executable path.
        ///
        /// Throws <see cref="ShimBuildException"/> with the compiler's own
        /// output on failure: a silent failure would surface as every agent
        /// command dying, which is far harder to diagnose than a compiler
        /// error.
        /// </summary>
        public static string Build(string source, string target, string compiler)
        {
            if (compiler == null) compiler = FindCompiler();
            if (compiler == null)
            {
                throw new ShimBuildException(
                    "No C# compiler found. Looked for "
                    + @"%SystemRoot%\Microsoft.NET\Framework64\*\csc.exe and csc.exe on PATH. "
                    + "csc.exe ships with the .NET Framework on every Windows install; if it is "
                    + "missing, install the .NET Framework.");
            }

            if (!File.Exists(source))
                throw new ShimBuildException("Shim source not found: " + source);

            string targetDir = Path.GetDirectoryName(Path.GetFullPath(target));
            Directory.CreateDirectory(targetDir);

            // NOTE: argv[0] is deliberately NOT part of the argument string.
            // psi.FileName already names the compiler, and CreateProcessW
            // passes FileName as argv[0] itself. Including the path again here
            // makes csc treat it as a second INPUT FILE and fail with
            // "CS2020: Only the first set of input files can build a target
            // other than 'module'".
            var argv = new List<string>();
            argv.Add("/nologo");
            argv.Add("/target:exe");
            argv.Add("/platform:anycpu");
            argv.Add("/optimize+");
            // The /out: value is quoted AFTER the colon, i.e. /out:"C:\path".
            // Quoting the whole token ("/out:C:\path") makes csc read the
            // option name as `/"out:...` and fail with CS2007; leaving it
            // unquoted splits a path containing a space.
            argv.Add("/out:" + QuoteValue(target));
            argv.Add(QuoteValue(source));

            var stdOut = new StringBuilder();
            var stdErr = new StringBuilder();

            var psi = new ProcessStartInfo();
            psi.FileName = compiler;
            // Built as one string: this framework's ProcessStartInfo has no
            // ArgumentList, and every element here is a path we control.
            psi.Arguments = JoinArguments(argv);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;

            int exitCode;
            using (Process process = Process.Start(psi))
            {
                process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) stdOut.Append(e.Data).Append('\n');
                };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) stdErr.Append(e.Data).Append('\n');
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                exitCode = process.ExitCode;
            }

            if (exitCode != 0 || !File.Exists(target))
            {
                throw new ShimBuildException(
                    "Compiling the shim failed (exit " + exitCode + ").\n"
                    + "Command: " + psi.Arguments + "\n"
                    + (stdOut.ToString() + stdErr.ToString()).Trim());
            }

            return target;
        }

        /// <summary>Convenience overload that also resolves the compiler.</summary>
        public static string Build(string source, string target)
        {
            return Build(source, target, null);
        }

        /// <summary>
        /// Quote and join an argv for a Windows command line.
        ///
        /// An element that already carries its own quoting — `/out:"C:\a b\x.exe"`
        /// — is emitted verbatim. Re-quoting it would wrap the quotes again
        /// ("\"/out:...\""), which csc rejects with CS2021.
        /// </summary>
        private static string JoinArguments(IList<string> argv)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < argv.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(argv[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Quote a value destined to follow an option's colon, e.g. /out:.
        ///
        /// The quotes wrap only the value, never the option name: csc parses
        /// `/out:"C:\a b\x.exe"` but rejects `"/out:C:\a b\x.exe"`.
        /// </summary>
        private static string QuoteValue(string value)
        {
            if (value.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0) return value;
            return "\"" + value + "\"";
        }
    }
}
