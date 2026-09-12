// msys2_shell_shim — the argv[0] that lets a DSH agent preset reach MSYS2.
//
// WHY THIS EXISTS
//
// DSH's pwsh executor spawns one fixed argv per command:
//
//     [<pwshPath>, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", <command>]
//
// with no shell involved. Three consequences force this program to exist:
//
//   1. `pwshPath` is used verbatim as argv[0] of a direct CreateProcessW, so
//      it cannot be a .cmd/.bat — Node's spawn rejects those with EINVAL.
//      msys2_shell.cmd IS a .cmd, so it cannot be argv[0].
//   2. The command arrives with PowerShell's encoding preamble prepended, and
//      bash cannot parse that PowerShell text.
//   3. msys2_shell.cmd is a batch file, so the command would have to survive
//      cmd.exe's argument parser AND bash's. Escaping for both at once is
//      fragile: a command containing double quotes comes back mangled.
//
// HOW IT WORKS
//
// Rather than fight two parsers, this shim writes the command to a temporary
// .sh file in MSYS2's /tmp and passes msys2_shell.cmd only a SHORT, SIMPLE
// PATH with no spaces, quotes, or metacharacters:
//
//     msys2_shell.cmd -defterm -no-start -use-full-path <flavour> -here -c /tmp/dsh-<id>.sh
//
// The command bytes therefore never travel through cmd.exe's parser, so no
// escaping can corrupt them. The file is written UTF-8 with LF endings and no
// BOM, which is what bash expects; a BOM would become a syntax error on line 1.
//
// Exit-code propagation: `-no-start` makes msys2_shell.cmd return the login
// shell's own status, and this shim returns that verbatim, which is what makes
// the tool layer's `[exit code: N]` reporting meaningful.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

internal static class Msys2ShellShim
{
    private static int Main(string[] args)
    {
        // The command is the final argument. DSH always supplies one; without
        // it there is nothing to run, and guessing would be worse than failing.
        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                "msys2_shell_shim: no command argument received. This program is meant to be "
                + "invoked by a DSH agent preset, not by hand.");
            return 2;
        }

        string command = args[args.Length - 1];

        // (2) Strip the PowerShell encoding preamble the pwsh executor prepends.
        command = StripPowerShellPreamble(command);

        // Configuration travels through the environment so the same binary
        // serves every flavour.
        string shellCmd = Environment.GetEnvironmentVariable("DSH_MSYS2_SHELL_CMD");
        if (string.IsNullOrEmpty(shellCmd))
        {
            Console.Error.WriteLine(
                "msys2_shell_shim: DSH_MSYS2_SHELL_CMD is not set. Reinstall the MSYS2 agent "
                + "profile, or set it to the full path of msys2_shell.cmd.");
            return 2;
        }
        if (!File.Exists(shellCmd))
        {
            Console.Error.WriteLine("msys2_shell_shim: msys2_shell.cmd not found at " + shellCmd);
            return 2;
        }

        // The MSYS2 root is msys2_shell.cmd's parent.
        string msysRoot = Path.GetDirectoryName(Path.GetFullPath(shellCmd));

        // A unique name per invocation: concurrent agents must not collide.
        string scriptName = "dsh-msys2-" + Guid.NewGuid().ToString("N") + ".sh";

        // Pick a directory bash can read AND this process can write. MSYS2's
        // own /tmp is preferred. It is normally writable, but a confined host
        // (or a locked-down install) can deny it, so the Windows temp
        // directory is a fallback. Each candidate carries both the path we
        // write to and the path bash sees, because a Windows path is not
        // usable inside MSYS2 and vice versa.
        string[] candidateDirs =
        {
            Path.Combine(msysRoot, "tmp"),                          // MSYS2 /tmp
            Path.Combine(Path.GetTempPath(), "dsh-msys2"),         // %TEMP%\dsh-msys2
        };

        string scriptPath = null;
        string msysScriptPath = null;
        var problems = new StringBuilder();

        foreach (string dir in candidateDirs)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, scriptName);
                // UTF-8 without a BOM, LF line endings. A BOM would appear as
                // characters on line 1 and break the script.
                File.WriteAllText(
                    probe,
                    command.EndsWith("\n", StringComparison.Ordinal) ? command : command + "\n",
                    new UTF8Encoding(false));
                scriptPath = probe;
                msysScriptPath = ToMsysPath(probe, msysRoot);
                break;
            }
            catch (Exception error)
            {
                problems.Append("  ").Append(dir).Append(": ").Append(error.Message).Append('\n');
            }
        }

        if (scriptPath == null)
        {
            Console.Error.WriteLine(
                "msys2_shell_shim: cannot write the temporary command file. Tried:\n" + problems);
            return 2;
        }

        try
        {
            string msystem = Environment.GetEnvironmentVariable("DSH_MSYS2_MSYSTEM");
            string flavourFlag = FlavourFlag(msystem);

            // (3) The only thing crossing cmd.exe's parser is this short path.
            // It contains no spaces, quotes, or metacharacters, so quoting is a
            // non-issue and cannot corrupt the command.
            var line = new StringBuilder();
            line.Append("-defterm -no-start -use-full-path");
            if (flavourFlag != null) line.Append(' ').Append(flavourFlag);
            line.Append(" -here -c ").Append(msysScriptPath);

            var psi = new ProcessStartInfo
            {
                FileName = shellCmd,
                Arguments = line.ToString(),
                UseShellExecute = false,
                // Inherit this process's stdio so DSH captures bash's output
                // through the handles it already set up.
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                RedirectStandardInput = false,
                // Deliberately NOT changing directory: CreateProcessW gave this
                // process the cwd DSH asked for, and `-here` makes
                // msys2_shell.cmd honour it, so the agent's `workdir` reaches
                // bash intact.
                WorkingDirectory = Directory.GetCurrentDirectory(),
            };

            using (Process child = Process.Start(psi))
            {
                child.WaitForExit();
                return child.ExitCode;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("msys2_shell_shim: failed to run " + shellCmd + ": " + error.Message);
            return 2;
        }
        finally
        {
            // Best-effort cleanup; a leftover file in /tmp is harmless and must
            // never mask the command's real exit code.
            try { File.Delete(scriptPath); } catch { }
        }
    }

    /// <summary>
    /// Remove DSH's PowerShell encoding preamble from the front of a command.
    ///
    /// The pwsh executor builds `&lt;ENCODING_PREAMBLE&gt;&lt;command&gt;` and that
    /// preamble is PowerShell source. Bash cannot parse it, so it must go
    /// before the command reaches bash.
    ///
    /// Only an exact, recognized prefix is stripped. If no prefix matches, the
    /// command is returned unchanged rather than guessed at.
    /// </summary>
    private static string StripPowerShellPreamble(string command)
    {
        for (int i = 0; i < PreamblePrefixes.Length; i++)
        {
            string prefix = PreamblePrefixes[i];
            if (command.StartsWith(prefix, StringComparison.Ordinal))
                return command.Substring(prefix.Length);
        }
        return command;
    }

    /// <summary>
    /// The preambles worth recognizing, longest first.
    ///
    /// The first entry is the preamble emitted by the DSH build this installer
    /// targets. The rest are narrower variants of the same intent, kept so a
    /// small upstream change in the preamble's exact text does not silently
    /// reintroduce a syntax error on every command.
    /// </summary>
    private static readonly string[] PreamblePrefixes = new string[]
    {
        "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); "
            + "$OutputEncoding = [System.Text.UTF8Encoding]::new($false); ",
        "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false);",
        "$OutputEncoding = [System.Text.UTF8Encoding]::new($false);",
    };

    /// <summary>
    /// Convert a Windows path into the POSIX path bash sees.
    ///
    /// MSYS2 maps the drive holding its own install root to <c>/</c>, so a
    /// file under <c>&lt;root&gt;\tmp\</c> is <c>/tmp/…</c>, while a file on
    /// another drive appears under <c>/&lt;drive&gt;/…</c>. Handing bash a
    /// Windows path would lose every backslash — it would read
    /// <c>C:\Users\…</c> as the single token <c>C:Users…</c> and report
    /// "command not found".
    /// </summary>
    private static string ToMsysPath(string windowsPath, string msysRoot)
    {
        string full = Path.GetFullPath(windowsPath);
        string root = Path.GetFullPath(msysRoot);

        // Inside the MSYS2 install root: strip the root and use the tail.
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            string tail = full.Substring(root.Length).Replace('\\', '/');
            if (!tail.StartsWith("/", StringComparison.Ordinal)) tail = "/" + tail;
            return tail;
        }

        // Anywhere else: /<drive-letter>/rest, which is MSYS2's convention.
        string drive = full.Substring(0, 1).ToLowerInvariant();
        string rest = full.Substring(2).Replace('\\', '/');
        return "/" + drive + rest;
    }

    /// <summary>Map an MSYSTEM value onto its msys2_shell.cmd flag.</summary>
    private static string FlavourFlag(string msystem)
    {
        switch (msystem)
        {
            case "MINGW64": return "-mingw64";
            case "MINGW32": return "-mingw32";
            case "UCRT64": return "-ucrt64";
            case "CLANG64": return "-clang64";
            case "CLANGARM64": return "-clangarm64";
            case "MSYS": return "-msys";
            default: return null; // Let msys2_shell.cmd pick its own default.
        }
    }
}
