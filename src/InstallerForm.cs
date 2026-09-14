// InstallerForm — the WinForms front end for the MSYS2 agent-profile installer.
//
// The whole job is: find msys2_shell.cmd (PATH first, then a file dialog), let
// the user tick which flavours to install, then write five preset directories
// into %USERPROFILE%\.dsh\.agent-presets so DSH's roster offers them.
//
// All the logic lives in PresetWriter/Msys2Discovery so the test harness can
// exercise it without a GUI.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Dsh.Msys2Installer
{
    public sealed class InstallerForm : Form
    {
        private readonly TextBox _shellCmdBox;
        private readonly Button _browseButton;
        private readonly Label _statusLabel;
        private readonly List<CheckBox> _flavourBoxes = new List<CheckBox>();
        private readonly TextBox _logBox;
        private readonly Button _installButton;
        private readonly Label _targetLabel;

        public InstallerForm()
        {
            Text = "DSH MSYS2 Agent Profile Installer";
            ClientSize = new Size(720, 640);
            MinimumSize = new Size(660, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            int y = 12;

            // ── heading ────────────────────────────────────────────────────────
            var title = new Label();
            title.Text = "Install MSYS2 agent profiles for DSH";
            title.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(14, y);
            Controls.Add(title);
            y += 32;

            var intro = new Label();
            intro.Text =
                "Each selected flavour becomes a preset you can pick in the DSH roster.\n"
                + "Presets are written to " + DshPaths.PresetRoot() + ".";
            intro.AutoSize = true;
            intro.ForeColor = Color.FromArgb(70, 70, 70);
            intro.Location = new Point(16, y);
            Controls.Add(intro);
            y += 40;

            // ── msys2_shell.cmd ───────────────────────────────────────────────
            var shellLabel = new Label();
            shellLabel.Text = "msys2_shell.cmd:";
            shellLabel.AutoSize = true;
            shellLabel.Location = new Point(16, y + 3);
            Controls.Add(shellLabel);

            _shellCmdBox = new TextBox();
            _shellCmdBox.Location = new Point(130, y);
            _shellCmdBox.Width = 460;
            _shellCmdBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _shellCmdBox.TextChanged += OnShellCmdChanged;
            Controls.Add(_shellCmdBox);

            _browseButton = new Button();
            _browseButton.Text = "Browse…";
            _browseButton.Location = new Point(598, y - 1);
            _browseButton.Width = 100;
            _browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browseButton.Click += OnBrowse;
            Controls.Add(_browseButton);
            y += 30;

            _statusLabel = new Label();
            _statusLabel.AutoSize = false;
            _statusLabel.Location = new Point(130, y);
            _statusLabel.Size = new Size(568, 34);
            _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _statusLabel.ForeColor = Color.FromArgb(70, 70, 70);
            Controls.Add(_statusLabel);
            y += 42;

            // ── flavours ──────────────────────────────────────────────────────
            var flavourLabel = new Label();
            flavourLabel.Text = "Flavours to install:";
            flavourLabel.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            flavourLabel.AutoSize = true;
            flavourLabel.Location = new Point(16, y);
            Controls.Add(flavourLabel);
            y += 24;

            foreach (Msys2Flavour flavour in Msys2Flavours.All)
            {
                var box = new CheckBox();
                box.Text = flavour.Label + "  —  " + flavour.Blurb;
                box.Checked = true;
                box.AutoSize = true;
                box.Location = new Point(30, y);
                box.Tag = flavour;
                _flavourBoxes.Add(box);
                Controls.Add(box);
                y += 24;
            }
            y += 6;

            // ── full-access warning ───────────────────────────────────────────
            var warning = new Label();
            warning.AutoSize = false;
            warning.Location = new Point(16, y);
            warning.Size = new Size(688, 52);
            warning.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            warning.ForeColor = Color.FromArgb(150, 60, 0);
            warning.Text =
                "Note: these profiles run unconfined (danger-full-access). MSYS2 cannot start\n"
                + "under the Windows ACL sandbox — msys-2.0.dll needs a per-user named section the\n"
                + "write-restricted token denies — so no file-write boundary is enforced.";
            Controls.Add(warning);
            y += 58;

            // ── install ───────────────────────────────────────────────────────
            _targetLabel = new Label();
            _targetLabel.AutoSize = true;
            _targetLabel.ForeColor = Color.FromArgb(70, 70, 70);
            _targetLabel.Location = new Point(16, y);
            _targetLabel.Text = "Target: " + DshPaths.PresetRoot();
            Controls.Add(_targetLabel);
            y += 24;

            _installButton = new Button();
            _installButton.Text = "Install";
            _installButton.Location = new Point(16, y);
            _installButton.Size = new Size(120, 30);
            _installButton.Click += OnInstall;
            Controls.Add(_installButton);

            var uninstallButton = new Button();
            uninstallButton.Text = "Uninstall selected";
            uninstallButton.Location = new Point(146, y);
            uninstallButton.Size = new Size(140, 30);
            uninstallButton.Click += OnUninstall;
            Controls.Add(uninstallButton);

            var openButton = new Button();
            openButton.Text = "Open target folder";
            openButton.Location = new Point(296, y);
            openButton.Size = new Size(140, 30);
            openButton.Click += OnOpenTarget;
            Controls.Add(openButton);
            y += 40;

            // ── log ───────────────────────────────────────────────────────────
            _logBox = new TextBox();
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.Location = new Point(16, y);
            _logBox.Size = new Size(688, ClientSize.Height - y - 16);
            _logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _logBox.Font = new Font("Consolas", 8.5f);
            _logBox.BackColor = Color.FromArgb(250, 250, 250);
            Controls.Add(_logBox);

            // Pre-fill discovery. A machine with MSYS2 on PATH needs no clicks.
            string guessed = Msys2Discovery.Guess();
            if (guessed != null)
            {
                _shellCmdBox.Text = guessed;
                Log("Found msys2_shell.cmd automatically.");
            }
            else
            {
                Log("msys2_shell.cmd was not found on PATH or in the usual locations.");
                Log("Use Browse… to select it. It is at the top of your MSYS2 install root,");
                Log("for example: C:\\msys64\\msys2_shell.cmd");
            }
            Revalidate();
        }

        private void Log(string line)
        {
            _logBox.AppendText(line + Environment.NewLine);
        }

        private void OnShellCmdChanged(object sender, EventArgs e)
        {
            Revalidate();
        }

        /// <summary>
        /// Validate the current path and show the verdict. This is what turns a
        /// wrong pick into an explanation rather than a failed install.
        /// </summary>
        private void Revalidate()
        {
            string reason = Msys2Discovery.Validate(_shellCmdBox.Text);
            if (reason == null)
            {
                _statusLabel.ForeColor = Color.FromArgb(0, 110, 0);
                _statusLabel.Text = "OK — MSYS2 root: "
                    + Path.GetDirectoryName(Path.GetFullPath(_shellCmdBox.Text));
                _installButton.Enabled = true;
            }
            else
            {
                _statusLabel.ForeColor = Color.FromArgb(170, 0, 0);
                _statusLabel.Text = reason;
                _installButton.Enabled = false;
            }
        }

        /// <summary>
        /// The manual picker, used when discovery fails. It opens in the most
        /// likely directory so the usual case is still two clicks.
        /// </summary>
        private void OnBrowse(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select msys2_shell.cmd";
                dialog.Filter = "MSYS2 shell launcher (msys2_shell.cmd)|msys2_shell.cmd|All files (*.*)|*.*";
                dialog.CheckFileExists = true;

                string current = _shellCmdBox.Text;
                if (!string.IsNullOrEmpty(current))
                {
                    string dir = Path.GetDirectoryName(current);
                    if (Directory.Exists(dir)) dialog.InitialDirectory = dir;
                }
                if (string.IsNullOrEmpty(dialog.InitialDirectory))
                {
                    foreach (string candidate in Msys2Discovery.WellKnownLocations())
                    {
                        string dir = Path.GetDirectoryName(candidate);
                        if (Directory.Exists(dir))
                        {
                            dialog.InitialDirectory = dir;
                            break;
                        }
                    }
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _shellCmdBox.Text = dialog.FileName;
                    Log("Selected: " + dialog.FileName);

                    // Remember the pick, so Browse is a one-time cost rather
                    // than something to repeat on every run.
                    if (Msys2Discovery.Validate(dialog.FileName) == null)
                    {
                        string saved = DshConfig.SaveMsys2Root(dialog.FileName);
                        if (saved != null) Log("Recorded in " + saved);
                    }
                    Revalidate();
                }
            }
        }

        private List<Msys2Flavour> SelectedFlavours()
        {
            var chosen = new List<Msys2Flavour>();
            foreach (CheckBox box in _flavourBoxes)
                if (box.Checked) chosen.Add((Msys2Flavour)box.Tag);
            return chosen;
        }

        private void OnInstall(object sender, EventArgs e)
        {
            string shellCmd = _shellCmdBox.Text;
            string reason = Msys2Discovery.Validate(shellCmd);
            if (reason != null)
            {
                MessageBox.Show(this, reason, "Invalid msys2_shell.cmd",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            List<Msys2Flavour> chosen = SelectedFlavours();
            if (chosen.Count == 0)
            {
                MessageBox.Show(this, "Select at least one flavour to install.",
                    "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _installButton.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                string presetRoot = DshPaths.PresetRoot();
                Log("");
                Log("Installing " + chosen.Count + " preset(s) into " + presetRoot);

                List<InstallResult> results = PresetWriter.InstallAll(
                    chosen, shellCmd, presetRoot,
                    delegate(Msys2Flavour flavour, int index, int total)
                    {
                        Log("  [" + index + "/" + total + "] " + flavour.PresetId);
                        Application.DoEvents();
                    });

                Log("");
                foreach (InstallResult result in results)
                {
                    Log("Installed " + result.Flavour.PresetId + " -> " + result.PresetDir);
                    foreach (string file in result.Written)
                        Log("    " + Path.GetFileName(file));
                }

                Log("");
                Log("Done. Restart `dsh web` (or refresh the roster) and pick a profile:");
                foreach (InstallResult result in results)
                    Log("    " + result.Flavour.Label);

                MessageBox.Show(this,
                    "Installed " + results.Count + " MSYS2 agent profile(s).\n\n"
                    + "They are now selectable in the DSH roster. Restart the harness if the\n"
                    + "list does not refresh.",
                    "Install complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (ShimBuildException error)
            {
                Log("");
                Log("FAILED: " + error.Message);
                MessageBox.Show(this, error.Message, "Could not build the shell shim",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception error)
            {
                Log("");
                Log("FAILED: " + error.Message);
                MessageBox.Show(this, error.ToString(), "Install failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                Revalidate();
            }
        }

        private void OnUninstall(object sender, EventArgs e)
        {
            List<Msys2Flavour> chosen = SelectedFlavours();
            if (chosen.Count == 0)
            {
                MessageBox.Show(this, "Select at least one flavour to uninstall.",
                    "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string presetRoot = DshPaths.PresetRoot();
            var removed = new List<string>();
            foreach (Msys2Flavour flavour in chosen)
                if (PresetWriter.Uninstall(flavour, presetRoot))
                    removed.Add(flavour.PresetId);

            Log("");
            if (removed.Count == 0)
            {
                Log("Nothing to remove — no matching presets were installed.");
            }
            else
            {
                Log("Removed: " + string.Join(", ", removed.ToArray()));
            }
        }

        private void OnOpenTarget(object sender, EventArgs e)
        {
            string root = DshPaths.PresetRoot();
            try
            {
                Directory.CreateDirectory(root);
                Process.Start("explorer.exe", "\"" + root + "\"");
            }
            catch (Exception error)
            {
                Log("Could not open " + root + ": " + error.Message);
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // A headless mode, so the installer can be exercised from a script
            // and from the test harness without opening a window.
            //
            //   msys2-installer.exe --install --shell-cmd <path> [--flavour k]...
            //   msys2-installer.exe --list
            if (args.Length > 0 && (args[0] == "--install" || args[0] == "--list"))
                return RunHeadless(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerForm());
            return 0;
        }

        /// <summary>
        /// Drive an install without the GUI. Prints a report and returns 0 on
        /// success, 1 on failure — so a caller can assert on it.
        /// </summary>
        private static int RunHeadless(string[] args)
        {
            string shellCmd = null;
            bool explicitShellCmd = false;
            var keys = new List<string>();

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--shell-cmd" && i + 1 < args.Length) { shellCmd = args[++i]; explicitShellCmd = true; }
                else if (args[i] == "--flavour" && i + 1 < args.Length) keys.Add(args[++i]);
            }

            if (args[0] == "--list")
            {
                foreach (Msys2Flavour flavour in Msys2Flavours.All)
                    Console.WriteLine(flavour.Key + "\t" + flavour.Msystem + "\t" + flavour.PresetId
                        + "\t" + flavour.Label);
                return 0;
            }

            if (shellCmd == null) shellCmd = Msys2Discovery.Guess();
            string reason = Msys2Discovery.Validate(shellCmd);
            if (reason != null)
            {
                Console.Error.WriteLine("error: " + reason);
                Console.Error.WriteLine();
                Console.Error.WriteLine(Msys2Discovery.DescribeFailure());
                return 1;
            }

            // An explicit --shell-cmd is a decision the user made; record it so
            // the next run finds the same install without being told again.
            if (explicitShellCmd)
            {
                string saved = DshConfig.SaveMsys2Root(shellCmd);
                Console.WriteLine("recorded " + DshConfig.RootKey + " in "
                    + (saved ?? "(nowhere: no writable " + Msys2Discovery.EnvFileName + ")"));
            }

            var chosen = new List<Msys2Flavour>();
            foreach (Msys2Flavour flavour in Msys2Flavours.All)
                if (keys.Count == 0 || keys.Contains(flavour.Key)) chosen.Add(flavour);

            try
            {
                string presetRoot = DshPaths.PresetRoot();
                Console.WriteLine("preset root: " + presetRoot);
                foreach (InstallResult result in PresetWriter.InstallAll(
                    chosen, shellCmd, presetRoot,
                    delegate(Msys2Flavour f, int i, int n)
                    {
                        Console.WriteLine("  [" + i + "/" + n + "] " + f.PresetId);
                    }))
                {
                    Console.WriteLine("installed " + result.PresetDir);
                }
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("error: " + error.Message);
                return 1;
            }
        }
    }
}
