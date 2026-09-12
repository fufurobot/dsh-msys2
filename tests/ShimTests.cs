// ShimTests — the tests that actually execute MSYS2 bash through the shim.
//
// These are the tests that prove the installer's central claim: that a command
// handed to msys2_shell_shim.exe the way DSH's pwsh executor hands it reaches
// the right MSYS2 environment, produces the right output, and returns the
// right exit code.
//
// They need an unconfined process. MSYS2's msys-2.0.dll cannot start under the
// Windows ACL sandbox (it needs a per-user named section the write-restricted
// token denies, giving Win32 error 5), so a confined run fails at process
// start. Each test checks for that condition and reports it as a skip with the
// reason, rather than passing silently or failing confusingly.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Dsh.Msys2Installer.Tests
{
    [TestClass]
    public sealed class ShimTests
    {
        /// <summary>The exact preamble DSH's pwsh executor prepends.</summary>
        private const string PowerShellPreamble =
            "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); "
            + "$OutputEncoding = [System.Text.UTF8Encoding]::new($false); ";

        /// <summary>A compiled shim plus the MSYS2 install it targets.</summary>
        private sealed class Fixture
        {
            public string Shim;
            public string ShellCmd;
            public string TempDir;
        }

        /// <summary>
        /// Compile the shim once per run and locate the real MSYS2 install.
        /// Returns null when either is unavailable, so the caller can skip.
        /// </summary>
        private static Fixture MakeFixture(out string skipReason)
        {
            skipReason = null;

            string shellCmd = Msys2Discovery.Guess();
            if (shellCmd == null)
            {
                skipReason = "no MSYS2 install found on this machine";
                return null;
            }

            var fixture = new Fixture();
            fixture.ShellCmd = shellCmd;
            fixture.TempDir = Path.Combine(TestScratch.Root(), "dsh-shim-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture.TempDir);
            fixture.Shim = Path.Combine(fixture.TempDir, "msys2_shell_shim.exe");

            try
            {
                ShimBuilder.Build(ShimBuilder.SourcePath(), fixture.Shim);
            }
            catch (Exception error)
            {
                skipReason = "could not compile the shim: " + error.Message;
                return null;
            }
            return fixture;
        }

        /// <summary>Run a command exactly as DSH's pwsh executor would.</summary>
        private static ProcessResult RunShim(Fixture fixture, string command, string msystem)
        {
            var psi = new ProcessStartInfo();
            psi.FileName = fixture.Shim;
            psi.Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"" + command.Replace("\"", "\\\"") + "\"";
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;

            // The preset supplies these; the shim reads them.
            psi.EnvironmentVariables["DSH_MSYS2_SHELL_CMD"] = fixture.ShellCmd;
            psi.EnvironmentVariables["DSH_MSYS2_MSYSTEM"] = msystem;
            // msys2_shell.cmd reads MSYSTEM from the environment too, and a
            // stale inherited value would override the flag we pass.
            psi.EnvironmentVariables.Remove("MSYSTEM");

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            using (Process process = Process.Start(psi))
            {
                process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) stdout.Append(e.Data).Append('\n');
                };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) stderr.Append(e.Data).Append('\n');
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(120000))
                {
                    try { process.Kill(); } catch (Exception) { }
                    throw new AssertionException("the command timed out after 120s: " + command);
                }

                var result = new ProcessResult();
                result.ExitCode = process.ExitCode;
                result.StdOut = stdout.ToString();
                result.StdErr = stderr.ToString();
                return result;
            }
        }

        private sealed class ProcessResult
        {
            public int ExitCode;
            public string StdOut;
            public string StdErr;

            public string All { get { return StdOut + StdErr; } }

            /// <summary>
            /// Whether MSYS2 failed to start because the process is confined.
            /// This is the documented ACL-sandbox signature.
            /// </summary>
            public bool IsSandboxBlocked()
            {
                return All.IndexOf("CreateFileMapping", StringComparison.Ordinal) >= 0
                    && All.IndexOf("Win32 error 5", StringComparison.Ordinal) >= 0;
            }
        }

        /// <summary>
        /// Run one command, treating a confined environment as a skip. Returns
        /// null when the environment cannot run MSYS2 at all.
        /// </summary>
        private static ProcessResult RunOrSkip(
            Fixture fixture, string command, string msystem, string label)
        {
            ProcessResult result = RunShim(fixture, PowerShellPreamble + command, msystem);
            if (result.IsSandboxBlocked())
            {
                throw new SkipException(
                    "MSYS2 cannot start in a confined process (msys-2.0.dll, Win32 error 5); "
                    + "run the test suite unconfined to execute this check");
            }
            return result;
        }

        private static void WithFixture(Action<Fixture> body)
        {
            string skipReason;
            Fixture fixture = MakeFixture(out skipReason);
            if (fixture == null) throw new SkipException(skipReason);

            try
            {
                body(fixture);
            }
            catch (SkipException)
            {
                throw;
            }
            finally
            {
                try { Directory.Delete(fixture.TempDir, true); } catch (Exception) { }
            }
        }

        // ── the checks that prove the shell works ─────────────────────────────

        [TestMethod]
        public void ReportsTheChosenEnvironment()
        {
            WithFixture(delegate(Fixture fixture)
            {
                ProcessResult r = RunOrSkip(fixture, "echo MSYSTEM=$MSYSTEM", "MINGW64", "msystem");
                Assert.AreEqual(0, r.ExitCode, "echo should succeed");
                Assert.Contains("MSYSTEM=MINGW64", r.StdOut, "the requested environment must be active");
            });
        }

        [TestMethod]
        public void EveryFlavourSelectsItsOwnEnvironmentAndPath()
        {
            // What this asserts is what the INSTALLER is responsible for: that
            // each flavour activates its own MSYSTEM and puts its own toolchain
            // directory on PATH. It deliberately does NOT assert that a
            // compiler exists there — that depends on which pacman packages the
            // user installed, which is not the installer's business.
            //
            // On this machine many toolchains are in fact absent: /mingw32/bin
            // and /ucrt64/bin are empty and /mingw64/bin has no gcc, so
            // `pacman -S mingw-w64-x86_64-gcc` (etc.) is a separate, user-owned
            // step.
            WithFixture(delegate(Fixture fixture)
            {
                foreach (Msys2Flavour flavour in Msys2Flavours.All)
                {
                    if (flavour.Key == "clangarm64") continue; // needs an ARM64 host

                    ProcessResult r = RunOrSkip(fixture,
                        "echo $MSYSTEM; case \":$PATH:\" in *:/" + flavour.Key + "/bin:*) echo ONPATH;; esac",
                        flavour.Msystem, flavour.Key);

                    Assert.Contains(flavour.Msystem, r.StdOut,
                        flavour.Key + " must report its MSYSTEM");
                    Assert.Contains("ONPATH", r.StdOut,
                        flavour.Key + " must put /" + flavour.Key + "/bin on PATH");
                }
            });
        }

        [TestMethod]
        public void ReportsWhichToolchainsAreActuallyInstalled()
        {
            // A diagnostic rather than an assertion: it records which toolchains
            // exist, so a "compiler not found" report can be attributed to a
            // missing pacman package instead of to the installer. It fails only
            // if the shell itself cannot run.
            WithFixture(delegate(Fixture fixture)
            {
                ProcessResult r = RunOrSkip(fixture,
                    "for d in /mingw64/bin /mingw32/bin /ucrt64/bin /clang64/bin; do "
                    + "if [ -f \"$d/gcc.exe\" ]; then echo \"$d: gcc present\"; "
                    + "else echo \"$d: no gcc\"; fi; done",
                    "MINGW64", "toolchains");

                Assert.AreEqual(0, r.ExitCode, "the probe must run: " + r.All);
                foreach (string dir in new string[] { "/mingw64/bin", "/mingw32/bin", "/ucrt64/bin", "/clang64/bin" })
                    Assert.Contains(dir, r.StdOut, "the probe should report on " + dir);
            });
        }

        [TestMethod]
        public void ReturnsTheCommandExitCode()
        {
            // Exit-code fidelity is what makes DSH's `[exit code: N]` reporting
            // meaningful; -no-start is what preserves it through the batch file.
            WithFixture(delegate(Fixture fixture)
            {
                Assert.AreEqual(0, RunOrSkip(fixture, "true", "MINGW64", "true").ExitCode, "true should be 0");
                Assert.AreEqual(7, RunOrSkip(fixture, "exit 7", "MINGW64", "exit 7").ExitCode, "exit 7 should be 7");
                Assert.AreEqual(1, RunOrSkip(fixture, "false", "MINGW64", "false").ExitCode, "false should be 1");
            });
        }

        [TestMethod]
        public void RunsPosixShellSyntax()
        {
            WithFixture(delegate(Fixture fixture)
            {
                // Uses paths that exist in MSYS2 rather than Linux paths: MSYS2
                // has no /etc/passwd, so testing for it would fail for a reason
                // that has nothing to do with the shim.
                ProcessResult r = RunOrSkip(fixture,
                    "for i in 1 2 3; do echo n=$i; done; "
                    + "if [ -d /usr/bin ]; then echo hasusrbin; fi; "
                    + "case \"$(uname -s)\" in MINGW*|MSYS*) echo ismsys;; esac",
                    "MINGW64", "posix");
                Assert.AreEqual(0, r.ExitCode, "the script should succeed: " + r.All);
                Assert.Contains("n=3", r.StdOut, "a for loop must run");
                Assert.Contains("hasusrbin", r.StdOut, "a test must run");
                Assert.Contains("ismsys", r.StdOut, "a case statement must run");
            });
        }

        [TestMethod]
        public void HandlesQuotesAndMetacharacters()
        {
            // These are the cases that broke an earlier escaping design: the
            // command has to survive cmd.exe's parser as well as bash's.
            WithFixture(delegate(Fixture fixture)
            {
                ProcessResult r = RunOrSkip(fixture,
                    "echo \"double quoted\"; echo 'single quoted'; printf '%s\\n' \"a 'b' c\"; "
                    + "echo a^b; echo '100%'; echo $((2+3)); echo one && echo two",
                    "MINGW64", "quoting");

                Assert.AreEqual(0, r.ExitCode, "the script should succeed: " + r.All);
                Assert.Contains("double quoted", r.StdOut, "double quotes must survive");
                Assert.Contains("single quoted", r.StdOut, "single quotes must survive");
                Assert.Contains("a 'b' c", r.StdOut, "nested quotes must survive");
                Assert.Contains("a^b", r.StdOut, "a caret must survive");
                Assert.Contains("100%", r.StdOut, "a percent sign must survive");
                Assert.Contains("5", r.StdOut, "arithmetic must run");
                Assert.Contains("two", r.StdOut, "&& must work");
            });
        }

        [TestMethod]
        public void HandlesPathsWithSpacesAndUnicode()
        {
            WithFixture(delegate(Fixture fixture)
            {
                ProcessResult r = RunOrSkip(fixture,
                    "mkdir -p \"/tmp/sp ace\" && ls -d \"/tmp/sp ace\" && echo 'héllo wörld'",
                    "MINGW64", "paths");
                Assert.AreEqual(0, r.ExitCode, "the script should succeed: " + r.All);
                Assert.Contains("sp ace", r.StdOut, "a path with spaces must work");
                Assert.Contains("héllo", r.StdOut, "non-ASCII output must survive");
            });
        }

        [TestMethod]
        public void StripsThePowerShellEncodingPreamble()
        {
            // DSH prepends PowerShell encoding statements to every command. Bash
            // cannot parse them, so the shim must remove them. Without this
            // every single command fails with a syntax error on `(`.
            WithFixture(delegate(Fixture fixture)
            {
                ProcessResult r = RunShim(fixture, PowerShellPreamble + "echo PREAMBLE_STRIPPED", "MINGW64");

                if (r.IsSandboxBlocked())
                    throw new SkipException("MSYS2 needs an unconfined process to run");

                Assert.AreEqual(0, r.ExitCode, "the command must run: " + r.All);
                Assert.Contains("PREAMBLE_STRIPPED", r.StdOut, "the command must execute");
                Assert.DoesNotContain("syntax error", r.All, "the preamble must not reach bash");
                Assert.DoesNotContain("OutputEncoding", r.StdOut, "the preamble must be stripped");
            });
        }

        [TestMethod]
        public void RunsPipelinesAndRedirection()
        {
            WithFixture(delegate(Fixture fixture)
            {
                ProcessResult r = RunOrSkip(fixture,
                    "printf 'a\\nb\\nc\\n' | grep b; echo redirected > /tmp/shim-test.txt; cat /tmp/shim-test.txt",
                    "MINGW64", "pipeline");
                Assert.AreEqual(0, r.ExitCode, "the script should succeed: " + r.All);
                Assert.Contains("b", r.StdOut, "a pipeline must work");
                Assert.Contains("redirected", r.StdOut, "redirection must work");
            });
        }

        [TestMethod]
        public void HonoursTheWorkingDirectory()
        {
            // The agent's `workdir` reaches bash through -here plus the spawn's
            // cwd; if it did not, every relative path an agent used would break.
            WithFixture(delegate(Fixture fixture)
            {
                string workdir = Path.Combine(fixture.TempDir, "workdir-test");
                Directory.CreateDirectory(workdir);

                var psi = new ProcessStartInfo();
                psi.FileName = fixture.Shim;
                psi.Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"pwd\"";
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = workdir;
                psi.EnvironmentVariables["DSH_MSYS2_SHELL_CMD"] = fixture.ShellCmd;
                psi.EnvironmentVariables["DSH_MSYS2_MSYSTEM"] = "MINGW64";
                psi.EnvironmentVariables.Remove("MSYSTEM");

                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (output.IndexOf("CreateFileMapping", StringComparison.Ordinal) >= 0)
                        throw new SkipException("MSYS2 needs an unconfined process to run");

                    // MSYS2 reports /c/Users/... style paths; assert on the leaf.
                    Assert.Contains("workdir-test", output, "bash should start in the spawned cwd");
                }
            });
        }

        [TestMethod]
        public void FailsClearlyWithoutConfiguration()
        {
            // A shim that silently did nothing when misconfigured would be far
            // harder to diagnose than one that refuses.
            WithFixture(delegate(Fixture fixture)
            {
                var psi = new ProcessStartInfo();
                psi.FileName = fixture.Shim;
                psi.Arguments = "-Command \"echo should-not-run\"";
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.EnvironmentVariables.Remove("DSH_MSYS2_SHELL_CMD");
                psi.EnvironmentVariables.Remove("DSH_MSYS2_MSYSTEM");

                using (Process process = Process.Start(psi))
                {
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    Assert.AreNotEqual(0, process.ExitCode, "a misconfigured shim must fail");
                    Assert.Contains("DSH_MSYS2_SHELL_CMD", error, "the error must name the missing variable");
                }
            });
        }
    }
}
