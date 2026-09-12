// InstallerTests — unit tests for discovery, path handling and preset writing.
//
// Everything here runs without MSYS2, so these are the checks that must hold
// on any machine. The tests that actually execute bash live in ShimTests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dsh.Msys2Installer.Tests
{
    [TestClass]
    public sealed class FlavourTests
    {
        [TestMethod]
        public void AllFiveRequestedFlavoursArePresent()
        {
            var keys = new List<string>();
            foreach (Msys2Flavour f in Msys2Flavours.All) keys.Add(f.Key);
            keys.Sort();

            Assert.AreEqual(
                "clang64,clangarm64,mingw32,mingw64,ucrt64",
                string.Join(",", keys.ToArray()),
                "the installer must cover exactly the five requested environments");
        }

        [TestMethod]
        public void DeclaredMsystemMatchesTheDeclaredShellKey()
        {
            // msys2_shell.cmd sets MSYSTEM from its flag, so a mismatch here
            // would silently build a preset for the wrong environment.
            foreach (Msys2Flavour f in Msys2Flavours.All)
            {
                Assert.AreEqual(
                    f.Key.ToUpperInvariant(), f.Msystem,
                    "flavour " + f.Key + " should export MSYSTEM=" + f.Key.ToUpperInvariant());
            }
        }

        [TestMethod]
        public void PresetIdsAreUniqueAndPrefixed()
        {
            var seen = new List<string>();
            foreach (Msys2Flavour f in Msys2Flavours.All)
            {
                Assert.IsTrue(f.PresetId.StartsWith("msys2-"), "preset id should be namespaced: " + f.PresetId);
                Assert.IsFalse(seen.Contains(f.PresetId), "duplicate preset id: " + f.PresetId);
                seen.Add(f.PresetId);
            }
        }

        [TestMethod]
        public void ShellFlagCarriesTheLeadingDash()
        {
            foreach (Msys2Flavour f in Msys2Flavours.All)
                Assert.AreEqual("-" + f.Key, f.ShellFlag, "shell flag for " + f.Key);
        }

        [TestMethod]
        public void PresetIdsAvoidTheShippedPresetNames()
        {
            // DSH's shipped roots win a duplicate id, so colliding with a
            // shipped preset (e.g. "cordis") would shadow this install.
            foreach (Msys2Flavour f in Msys2Flavours.All)
            {
                Assert.AreNotEqual("cordis", f.PresetId, "must not shadow a shipped preset");
                Assert.AreNotEqual("msys", f.PresetId, "must not shadow a shipped preset");
            }
        }
    }

    [TestClass]
    public sealed class PathTests
    {
        [TestMethod]
        public void DshHomeDefaultsToUserProfileDotDsh()
        {
            string saved = Environment.GetEnvironmentVariable("DSH_HOME");
            try
            {
                Environment.SetEnvironmentVariable("DSH_HOME", null);
                string expected = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
                Assert.AreEqual(expected, DshPaths.DshHome(), "default harness home");
            }
            finally
            {
                Environment.SetEnvironmentVariable("DSH_HOME", saved);
            }
        }

        [TestMethod]
        public void DshHomeHonoursTheEnvironmentOverride()
        {
            string saved = Environment.GetEnvironmentVariable("DSH_HOME");
            try
            {
                string custom = Path.Combine(TestScratch.Root(), "dsh-home-override-test");
                Environment.SetEnvironmentVariable("DSH_HOME", custom);
                Assert.AreEqual(custom, DshPaths.DshHome(), "DSH_HOME must win over the default");
            }
            finally
            {
                Environment.SetEnvironmentVariable("DSH_HOME", saved);
            }
        }

        [TestMethod]
        public void PresetRootIsTheAgentPresetsDirectory()
        {
            // This is where dsh-agent-presets looks; getting it wrong means the
            // presets are written somewhere DSH never scans.
            string saved = Environment.GetEnvironmentVariable("DSH_HOME");
            try
            {
                string custom = Path.Combine(TestScratch.Root(), "dsh-home-presetroot-test");
                Environment.SetEnvironmentVariable("DSH_HOME", custom);
                Assert.AreEqual(
                    Path.Combine(custom, ".agent-presets"),
                    DshPaths.PresetRoot(),
                    "preset root must be <dshHome>/.agent-presets");
            }
            finally
            {
                Environment.SetEnvironmentVariable("DSH_HOME", saved);
            }
        }
    }

    [TestClass]
    public sealed class DiscoveryTests
    {
        /// <summary>Build a fake MSYS2 root with the marker files discovery checks.</summary>
        internal static string MakeFakeMsysRoot(string label)
        {
            string root = Path.Combine(TestScratch.Root(), "dsh-msys2-test-" + label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "usr", "bin"));
            File.WriteAllText(Path.Combine(root, "msys2_shell.cmd"), "@echo off\r\nrem fake\r\n");
            File.WriteAllText(Path.Combine(root, "usr", "bin", "bash.exe"), "not a real exe");
            return root;
        }

        [TestMethod]
        public void ValidatesAGenuineLookingInstall()
        {
            string root = MakeFakeMsysRoot("valid");
            try
            {
                string cmd = Path.Combine(root, "msys2_shell.cmd");
                Assert.IsNull(Msys2Discovery.Validate(cmd), "a complete fake root should validate");
                Assert.IsTrue(Msys2Discovery.IsShellCmd(cmd), "IsShellCmd should accept it");
            }
            finally { Directory.Delete(root, true); }
        }

        [TestMethod]
        public void RejectsAFileWithTheWrongName()
        {
            string root = MakeFakeMsysRoot("wrongname");
            try
            {
                string other = Path.Combine(root, "something_else.cmd");
                File.WriteAllText(other, "x");
                string reason = Msys2Discovery.Validate(other);
                Assert.IsNotNull(reason, "a wrongly-named file must be rejected");
                Assert.Contains("msys2_shell.cmd", reason, "the reason should name the expected file");
            }
            finally { Directory.Delete(root, true); }
        }

        [TestMethod]
        public void RejectsAShellCmdWithNoBashBesideIt()
        {
            // The sibling check is what distinguishes a real MSYS2 root from a
            // stray copy, which would install a preset that cannot run.
            string root = Path.Combine(TestScratch.Root(), "dsh-msys2-nobash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string cmd = Path.Combine(root, "msys2_shell.cmd");
                File.WriteAllText(cmd, "@echo off\r\n");
                string reason = Msys2Discovery.Validate(cmd);
                Assert.IsNotNull(reason, "a root without usr\\bin\\bash.exe must be rejected");
                Assert.Contains("bash.exe", reason, "the reason should mention the missing bash.exe");
            }
            finally { Directory.Delete(root, true); }
        }

        [TestMethod]
        public void RejectsEmptyAndMissingPaths()
        {
            Assert.IsNotNull(Msys2Discovery.Validate(null), "null must be rejected");
            Assert.IsNotNull(Msys2Discovery.Validate(""), "empty must be rejected");
            Assert.IsNotNull(
                Msys2Discovery.Validate(Path.Combine(Path.GetTempPath(), "definitely-not-here-12345.cmd")),
                "a missing file must be rejected");
        }

        [TestMethod]
        public void WellKnownLocationsIncludeTheRealDownloadsLayout()
        {
            // The user's install lives at %USERPROFILE%\Downloads\msys64, so the
            // guess list must cover exactly that shape or discovery fails on the
            // machine this was written for.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string expected = Path.Combine(home, "Downloads", "msys64", "msys2_shell.cmd");

            var found = false;
            foreach (string candidate in Msys2Discovery.WellKnownLocations())
                if (string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase)) found = true;

            Assert.IsTrue(found, "expected " + expected + " among the well-known locations");
        }

        [TestMethod]
        public void FindOnPathReturnsNullWhenPathLacksTheShellCmd()
        {
            string saved = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", Path.GetTempPath());
                Assert.IsNull(Msys2Discovery.FindOnPath(), "a PATH without msys2_shell.cmd must yield null");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", saved);
            }
        }

        [TestMethod]
        public void FindOnPathLocatesTheShellCmd()
        {
            string root = MakeFakeMsysRoot("onpath");
            string saved = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", root + Path.PathSeparator + saved);
                string found = Msys2Discovery.FindOnPath();
                Assert.IsNotNull(found, "should find msys2_shell.cmd on PATH");
                Assert.AreEqual(
                    Path.Combine(root, "msys2_shell.cmd"), found,
                    "should return the PATH entry's msys2_shell.cmd");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", saved);
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void FindOnPathToleratesQuotedPathEntries()
        {
            // A quoted PATH entry is common on Windows and must not defeat
            // discovery.
            string root = MakeFakeMsysRoot("quotedpath");
            string saved = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", "\"" + root + "\"");
                string found = Msys2Discovery.FindOnPath();
                Assert.IsNotNull(found, "a quoted PATH entry should still be searched");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", saved);
                Directory.Delete(root, true);
            }
        }
    }

    [TestClass]
    public sealed class CompositionTests
    {
        private static string ShellCmd()
        {
            return @"C:\Users\test\Downloads\msys64\msys2_shell.cmd";
        }

        private static string ShimPath()
        {
            return @"C:\Users\test\.dsh\.agent-presets\msys2-mingw64\msys2_shell_shim.exe";
        }

        [TestMethod]
        public void CompositionTargetsTheShimNotMsys2ShellCmd()
        {
            // The whole point of the shim: pwshPath must be a real executable,
            // because Node refuses a .cmd as argv[0] (EINVAL).
            string yaml = PresetWriter.RenderComposition(Msys2Flavours.All[0], ShellCmd(), ShimPath());

            Assert.Contains("pwshPath: C:/Users/test/.dsh/.agent-presets/msys2-mingw64/msys2_shell_shim.exe",
                yaml, "pwshPath must point at the shim");
            Assert.DoesNotContain("pwshPath: " + PresetWriter.Posix(ShellCmd()),
                yaml, "pwshPath must NOT point at msys2_shell.cmd");
        }

        [TestMethod]
        public void CompositionEnablesThePwshPair()
        {
            string yaml = PresetWriter.RenderComposition(Msys2Flavours.All[0], ShellCmd(), ShimPath());

            Assert.Contains("- id: pwsh-sandbox", yaml, "the pwsh executor row must be present");
            Assert.Contains("- id: tool-pwsh", yaml, "the pwsh tool row must be present");
            Assert.DoesNotContain("disabled: true\n\n- id: tool-pwsh", yaml, "the pwsh tool must not be disabled");
        }

        [TestMethod]
        public void CompositionPinsDangerFullAccess()
        {
            // MSYS2 cannot start confined, so the preset must select full access.
            string yaml = PresetWriter.RenderComposition(Msys2Flavours.All[0], ShellCmd(), ShimPath());

            Assert.Contains("mode: danger-full-access", yaml, "the sandbox policy must be full access");
            Assert.Contains("policy: never", yaml, "full access implies no approval prompts");
        }

        [TestMethod]
        public void CompositionDocumentsWhyFullAccessIsRequired()
        {
            string yaml = PresetWriter.RenderComposition(Msys2Flavours.All[0], ShellCmd(), ShimPath());

            Assert.Contains("msys-2.0.dll", yaml, "the reason must be recorded in the file");
            Assert.Contains("Win32 error 5", yaml, "the exact failure must be recorded");
            Assert.Contains("CreateFileMapping", yaml, "the named-section cause must be recorded");
        }

        [TestMethod]
        public void PersonaTellsTheModelToWriteBashNotPowerShell()
        {
            string yaml = PresetWriter.RenderComposition(Msys2Flavours.All[0], ShellCmd(), ShimPath());

            Assert.Contains("pwsh", yaml, "the tool name must be acknowledged");
            Assert.Contains("does NOT run PowerShell", yaml, "the persona must say the tool is not PowerShell");
            Assert.Contains("bash", yaml, "the persona must direct the model to bash");
        }

        [TestMethod]
        public void PersonaTemplateTokensSurviveGeneration()
        {
            // {{model}} and {{cwd}} are DSH's own tokens; mangling them would
            // leave literal braces in the agent's system prompt.
            string yaml = PresetWriter.RenderComposition(Msys2Flavours.All[0], ShellCmd(), ShimPath());

            Assert.Contains("{{model}}", yaml, "the model token must survive");
            Assert.Contains("{{cwd}}", yaml, "the cwd token must survive");
            Assert.DoesNotContain("{{{{model}}}}", yaml, "braces must not be doubled");
        }

        [TestMethod]
        public void EachFlavourBakesInItsOwnEnvironment()
        {
            foreach (Msys2Flavour flavour in Msys2Flavours.All)
            {
                string yaml = PresetWriter.RenderComposition(flavour, ShellCmd(), ShimPath());
                Assert.Contains("DSH_MSYS2_MSYSTEM   = " + flavour.Msystem,
                    yaml, flavour.Key + " must document its own MSYSTEM");
                Assert.Contains("environment is " + flavour.Msystem,
                    yaml, flavour.Key + " persona must name its own environment");
            }
        }

        [TestMethod]
        public void CompositionUsesForwardSlashesInPaths()
        {
            // Windows paths in YAML would raise escaping questions; forward
            // slashes are accepted by Node, cmd.exe and bash alike.
            string yaml = PresetWriter.RenderComposition(Msys2Flavours.All[0], ShellCmd(), ShimPath());

            Assert.Contains("C:/Users/test/Downloads/msys64", yaml, "shell path should use forward slashes");
            Assert.DoesNotContain("C:\\Users\\test\\Downloads", yaml, "backslash paths should not appear");
        }

        [TestMethod]
        public void CompositionIsATopLevelListOfPluginRows()
        {
            // dsh-agent-presets rejects anything that is not a list of rows
            // carrying a plugin name, which would show as a broken roster row.
            string yaml = PresetWriter.RenderComposition(Msys2Flavours.All[0], ShellCmd(), ShimPath());
            string[] lines = yaml.Replace("\r\n", "\n").Split('\n');

            bool sawRow = false;
            foreach (string line in lines)
            {
                if (line.StartsWith("#")) continue;   // comments are fine
                if (line.StartsWith("  ")) continue;  // nested under a row
                if (line.Trim().Length == 0) continue;

                Assert.IsTrue(line.StartsWith("- id: "),
                    "every top-level line must start a plugin row, got: " + line);
                sawRow = true;
            }
            Assert.IsTrue(sawRow, "the composition must contain rows");
        }

        [TestMethod]
        public void EveryRowCarriesANameOrIsAGroup()
        {
            // The shape check dsh-agent-presets performs: each row needs a
            // `name`. A row without one is reported as broken.
            string yaml = PresetWriter.RenderComposition(Msys2Flavours.All[0], ShellCmd(), ShimPath());
            string[] lines = yaml.Replace("\r\n", "\n").Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("- id: ")) continue;

                // Look ahead for the row's `name:` at the same indent level.
                bool hasName = false;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (lines[j].StartsWith("- id: ")) break;
                    if (lines[j].StartsWith("  name: ")) { hasName = true; break; }
                }
                Assert.IsTrue(hasName, "row at line " + (i + 1) + " has no name: " + lines[i]);
            }
        }

        [TestMethod]
        public void CompositionHasNoTabs()
        {
            // YAML forbids tabs for indentation; one would break the preset.
            string yaml = PresetWriter.RenderComposition(Msys2Flavours.All[0], ShellCmd(), ShimPath());
            Assert.DoesNotContain("\t", yaml, "YAML must not contain tab characters");
        }

        [TestMethod]
        public void CompositionEndsWithANewline()
        {
            string yaml = PresetWriter.RenderComposition(Msys2Flavours.All[0], ShellCmd(), ShimPath());
            Assert.IsTrue(yaml.EndsWith("\n"), "a POSIX text file should end with a newline");
        }
    }

    [TestClass]
    public sealed class MetadataTests
    {
        [TestMethod]
        public void MetadataNamesTheFlavour()
        {
            string yaml = PresetWriter.RenderMetadata(Msys2Flavours.All[0]);
            Assert.Contains("name: 'MSYS2 · mingw64'", yaml, "metadata should name the flavour");
            Assert.Contains("description: 'MSYS2 / MinGW64", yaml, "metadata should describe the flavour");
        }

        [TestMethod]
        public void YamlQuoteEscapesEmbeddedQuotes()
        {
            // A single-quoted YAML scalar escapes a quote by doubling it. A
            // flavour blurb containing an apostrophe would otherwise corrupt
            // the file.
            Assert.AreEqual("'it''s'", PresetWriter.YamlQuote("it's"), "embedded quotes must be doubled");
            Assert.AreEqual("'plain'", PresetWriter.YamlQuote("plain"), "plain text is just quoted");
        }
    }

    [TestClass]
    public sealed class InstallTests
    {
        private static string MakeShellCmd(string root)
        {
            Directory.CreateDirectory(Path.Combine(root, "usr", "bin"));
            File.WriteAllText(Path.Combine(root, "msys2_shell.cmd"), "@echo off\r\n");
            File.WriteAllText(Path.Combine(root, "usr", "bin", "bash.exe"), "x");
            return Path.Combine(root, "msys2_shell.cmd");
        }

        [TestMethod]
        public void InstallWritesTheCompositionMetadataReadmeAndShim()
        {
            string msys = DiscoveryTests.MakeFakeMsysRoot("install");
            string presets = Path.Combine(TestScratch.Root(), "dsh-presets-" + Guid.NewGuid().ToString("N"));
            try
            {
                string shellCmd = Path.Combine(msys, "msys2_shell.cmd");
                InstallResult result = PresetWriter.Install(Msys2Flavours.All[0], shellCmd, presets, null);

                string dir = Path.Combine(presets, "msys2-mingw64");
                Assert.AreEqual(dir, result.PresetDir, "preset directory");
                Assert.FileExists(Path.Combine(dir, "agent.cordis.yml"), "composition must be written");
                Assert.FileExists(Path.Combine(dir, "preset.yml"), "metadata must be written");
                Assert.FileExists(Path.Combine(dir, "README.md"), "readme must be written");
                Assert.FileExists(Path.Combine(dir, "msys2_shell_shim.exe"), "the shim must be compiled");
            }
            finally
            {
                Directory.Delete(msys, true);
                if (Directory.Exists(presets)) Directory.Delete(presets, true);
            }
        }

        [TestMethod]
        public void GeneratedFilesUseLfEndingsAndNoBom()
        {
            string msys = DiscoveryTests.MakeFakeMsysRoot("lineendings");
            string presets = Path.Combine(TestScratch.Root(), "dsh-presets-" + Guid.NewGuid().ToString("N"));
            try
            {
                string shellCmd = Path.Combine(msys, "msys2_shell.cmd");
                PresetWriter.Install(Msys2Flavours.All[0], shellCmd, presets, null);

                string dir = Path.Combine(presets, "msys2-mingw64");
                foreach (string name in new string[] { "agent.cordis.yml", "preset.yml", "README.md" })
                {
                    byte[] bytes = File.ReadAllBytes(Path.Combine(dir, name));

                    Assert.IsFalse(
                        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                        name + " must not start with a UTF-8 BOM");

                    Assert.IsFalse(
                        Encoding.UTF8.GetString(bytes).Contains("\r\n"),
                        name + " must use LF line endings");
                }
            }
            finally
            {
                Directory.Delete(msys, true);
                if (Directory.Exists(presets)) Directory.Delete(presets, true);
            }
        }

        [TestMethod]
        public void InstallAllWritesEveryFlavour()
        {
            string msys = DiscoveryTests.MakeFakeMsysRoot("all");
            string presets = Path.Combine(TestScratch.Root(), "dsh-presets-" + Guid.NewGuid().ToString("N"));
            try
            {
                string shellCmd = Path.Combine(msys, "msys2_shell.cmd");
                List<InstallResult> results = PresetWriter.InstallAll(
                    new List<Msys2Flavour>(Msys2Flavours.All), shellCmd, presets, null);

                Assert.AreEqual(5, results.Count, "all five flavours should be installed");
                foreach (Msys2Flavour flavour in Msys2Flavours.All)
                {
                    Assert.FileExists(
                        Path.Combine(presets, flavour.PresetId, "agent.cordis.yml"),
                        flavour.PresetId + " composition");
                    Assert.FileExists(
                        Path.Combine(presets, flavour.PresetId, "msys2_shell_shim.exe"),
                        flavour.PresetId + " shim");
                }
            }
            finally
            {
                Directory.Delete(msys, true);
                if (Directory.Exists(presets)) Directory.Delete(presets, true);
            }
        }

        [TestMethod]
        public void InstallingTwiceOverwritesCleanly()
        {
            // Re-installing is the expected way to repair or reconfigure, so it
            // must not leave stale temporary files or fail.
            string msys = DiscoveryTests.MakeFakeMsysRoot("reinstall");
            string presets = Path.Combine(TestScratch.Root(), "dsh-presets-" + Guid.NewGuid().ToString("N"));
            try
            {
                string shellCmd = Path.Combine(msys, "msys2_shell.cmd");
                PresetWriter.Install(Msys2Flavours.All[0], shellCmd, presets, null);
                PresetWriter.Install(Msys2Flavours.All[0], shellCmd, presets, null);

                string dir = Path.Combine(presets, "msys2-mingw64");
                foreach (string stale in Directory.GetFiles(dir, "*.tmp"))
                    throw new AssertionException("a temporary file was left behind: " + stale);
            }
            finally
            {
                Directory.Delete(msys, true);
                if (Directory.Exists(presets)) Directory.Delete(presets, true);
            }
        }

        [TestMethod]
        public void UninstallRemovesThePresetAndReportsWhetherItDid()
        {
            string msys = DiscoveryTests.MakeFakeMsysRoot("uninstall");
            string presets = Path.Combine(TestScratch.Root(), "dsh-presets-" + Guid.NewGuid().ToString("N"));
            try
            {
                string shellCmd = Path.Combine(msys, "msys2_shell.cmd");
                PresetWriter.Install(Msys2Flavours.All[0], shellCmd, presets, null);

                Assert.IsTrue(PresetWriter.Uninstall(Msys2Flavours.All[0], presets), "first removal should report true");
                Assert.IsFalse(
                    Directory.Exists(Path.Combine(presets, "msys2-mingw64")),
                    "the preset directory should be gone");
                Assert.IsFalse(PresetWriter.Uninstall(Msys2Flavours.All[0], presets), "second removal should report false");
            }
            finally
            {
                Directory.Delete(msys, true);
                if (Directory.Exists(presets)) Directory.Delete(presets, true);
            }
        }

        [TestMethod]
        public void InstalledListsOnlyWhatIsActuallyThere()
        {
            string msys = DiscoveryTests.MakeFakeMsysRoot("installed");
            string presets = Path.Combine(TestScratch.Root(), "dsh-presets-" + Guid.NewGuid().ToString("N"));
            try
            {
                string shellCmd = Path.Combine(msys, "msys2_shell.cmd");
                Assert.AreEqual(0, PresetWriter.Installed(presets).Count, "nothing installed yet");

                PresetWriter.Install(Msys2Flavours.All[0], shellCmd, presets, null);
                PresetWriter.Install(Msys2Flavours.All[3], shellCmd, presets, null);

                List<Msys2Flavour> installed = PresetWriter.Installed(presets);
                Assert.AreEqual(2, installed.Count, "two presets should be reported");
                Assert.AreEqual("mingw64", installed[0].Key, "installed order follows the flavour list");
                Assert.AreEqual("clang64", installed[1].Key, "installed order follows the flavour list");
            }
            finally
            {
                Directory.Delete(msys, true);
                if (Directory.Exists(presets)) Directory.Delete(presets, true);
            }
        }

        [TestMethod]
        public void InstallLeavesNoStrayDirectoriesInThePresetRoot()
        {
            // DSH's discovery scans every directory under the preset root and
            // reports one without a composition as a broken roster row, so an
            // install must leave nothing there but the preset directories.
            string msys = DiscoveryTests.MakeFakeMsysRoot("clean");
            string presets = Path.Combine(TestScratch.Root(), "dsh-presets-" + Guid.NewGuid().ToString("N"));
            try
            {
                string shellCmd = Path.Combine(msys, "msys2_shell.cmd");
                PresetWriter.InstallAll(new List<Msys2Flavour>(Msys2Flavours.All), shellCmd, presets, null);

                var expected = new List<string>();
                foreach (Msys2Flavour flavour in Msys2Flavours.All) expected.Add(flavour.PresetId);
                expected.Sort();

                var actual = new List<string>();
                foreach (string dir in Directory.GetDirectories(presets))
                    actual.Add(Path.GetFileName(dir));
                actual.Sort();

                Assert.AreEqual(
                    string.Join(",", expected.ToArray()),
                    string.Join(",", actual.ToArray()),
                    "the preset root must contain exactly the five preset directories");

                // Every directory present must be a loadable preset.
                foreach (string dir in Directory.GetDirectories(presets))
                {
                    Assert.FileExists(
                        Path.Combine(dir, "agent.cordis.yml"),
                        "every directory in the preset root must hold a composition: " + dir);
                }
            }
            finally
            {
                Directory.Delete(msys, true);
                TestScratch.Cleanup(presets);
            }
        }

        [TestMethod]
        public void EachPresetGetsItsOwnShimCopy()
        {
            // A preset directory must be self-contained: deleting one must not
            // break another.
            string msys = DiscoveryTests.MakeFakeMsysRoot("shimcopy");
            string presets = Path.Combine(TestScratch.Root(), "dsh-presets-" + Guid.NewGuid().ToString("N"));
            try
            {
                string shellCmd = Path.Combine(msys, "msys2_shell.cmd");
                PresetWriter.InstallAll(new List<Msys2Flavour>(Msys2Flavours.All), shellCmd, presets, null);

                foreach (Msys2Flavour flavour in Msys2Flavours.All)
                {
                    string shim = Path.Combine(presets, flavour.PresetId, "msys2_shell_shim.exe");
                    Assert.FileExists(shim, flavour.PresetId + " shim");
                    Assert.IsTrue(new FileInfo(shim).Length > 0, flavour.PresetId + " shim must not be empty");
                }
            }
            finally
            {
                Directory.Delete(msys, true);
                if (Directory.Exists(presets)) Directory.Delete(presets, true);
            }
        }
    }

    [TestClass]
    public sealed class ShimBuilderTests
    {
        [TestMethod]
        public void FindsACompiler()
        {
            string compiler = ShimBuilder.FindCompiler();
            Assert.IsNotNull(compiler, "a C# compiler must be findable (csc.exe ships with Windows)");
            Assert.IsTrue(File.Exists(compiler), "the resolved compiler must exist: " + compiler);
        }

        [TestMethod]
        public void ShimSourceIsFindable()
        {
            string source = ShimBuilder.SourcePath();
            Assert.IsTrue(File.Exists(source), "the shim source must be locatable: " + source);
        }

        [TestMethod]
        public void CompilesTheShim()
        {
            string source = ShimBuilder.SourcePath();
            string dir = TestScratch.NewDirectory("shimbuild");
            try
            {
                string target = Path.Combine(dir, "shim.exe");
                string built = ShimBuilder.Build(source, target, null);

                Assert.AreEqual(target, built, "Build should return the target path");
                Assert.IsTrue(File.Exists(target), "the shim executable must exist");
                Assert.IsTrue(new FileInfo(target).Length > 0, "the shim must not be empty");
            }
            finally { TestScratch.Cleanup(dir); }
        }

        [TestMethod]
        public void CompilerIsNotPassedAsAnInputFile()
        {
            // Regression: passing the compiler path as the first argument while
            // ALSO setting ProcessStartInfo.FileName to it made csc read its
            // own path as a second source file and fail with
            // "CS2020: Only the first set of input files can build a target
            // other than 'module'". That broke every install.
            string compiler = ShimBuilder.FindCompiler();
            string source = ShimBuilder.SourcePath();
            string dir = TestScratch.NewDirectory("shimargv");
            try
            {
                string target = Path.Combine(dir, "broken.exe");

                // Reproduce the old, broken form directly: it must FAIL, which
                // is what proves this check is meaningful. The paths are quoted
                // properly so the ONLY thing wrong is the duplicated compiler.
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = compiler;
                psi.Arguments = "\"" + compiler + "\" /nologo /target:exe /optimize+ /out:"
                    + "\"" + target + "\" \"" + source + "\"";
                psi.UseShellExecute = false;
                // csc writes diagnostics to STDOUT, not stderr, so both must be
                // captured or the failure message comes back empty.
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;

                int brokenExit;
                string brokenErr;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    brokenErr = stdout + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    brokenExit = p.ExitCode;
                }
                Assert.AreNotEqual(0, brokenExit,
                    "duplicating the compiler path should fail — if this passes, the regression test proves nothing");
                Assert.Contains("CS2020", brokenErr, "the duplicate-input failure should be CS2020");

                // The fixed form must succeed.
                string good = Path.Combine(dir, "fixed.exe");
                ShimBuilder.Build(source, good, compiler);
                Assert.IsTrue(File.Exists(good), "the corrected build must produce the shim");
            }
            finally { TestScratch.Cleanup(dir); }
        }

        [TestMethod]
        public void CompilesIntoADirectoryContainingSpaces()
        {
            // A user's home directory often contains a space, and csc's /out:
            // must carry it through. This is the case that caught the
            // "/out:"-quoting bug.
            string source = ShimBuilder.SourcePath();
            string dir = TestScratch.NewDirectory("shim space");
            try
            {
                string target = Path.Combine(dir, "shim.exe");
                ShimBuilder.Build(source, target, null);
                Assert.IsTrue(File.Exists(target), "the shim must compile into a path containing a space: " + target);
            }
            finally { TestScratch.Cleanup(dir); }
        }

        [TestMethod]
        public void ReportsAMissingSourceFileClearly()
        {
            string dir = TestScratch.NewDirectory("shimmissing");
            try
            {
                Assert.Throws<ShimBuildException>(
                    delegate
                    {
                        ShimBuilder.Build(
                            Path.Combine(dir, "NoSuchFile.cs"),
                            Path.Combine(dir, "out.exe"),
                            null);
                    },
                    "a missing shim source must raise ShimBuildException");
            }
            finally { TestScratch.Cleanup(dir); }
        }
    }

    [TestClass]
    public sealed class PathDiscoveryIntegrationTests
    {
        [TestMethod]
        public void TheRealInstallOnThisMachineIsDiscovered()
        {
            // The user's MSYS2 lives at %USERPROFILE%\Downloads\msys64. If this
            // fails, auto-discovery on this machine is broken.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string expected = Path.Combine(home, "Downloads", "msys64", "msys2_shell.cmd");

            if (!File.Exists(expected))
                throw new AssertionException("expected MSYS2 at " + expected + " but it is not there");

            Assert.IsNull(Msys2Discovery.Validate(expected), "the real install should validate");
        }

        [TestMethod]
        public void GuessPrefersPathButFallsBackToTheRealInstall()
        {
            string guessed = Msys2Discovery.Guess();
            Assert.IsNotNull(guessed, "discovery should find MSYS2 on this machine");
            Assert.IsNull(Msys2Discovery.Validate(guessed), "the guessed path must be valid: " + guessed);
        }
    }
}
