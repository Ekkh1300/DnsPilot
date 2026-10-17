// DnsPilot setup - single-file installer, no third-party tooling.
//
// Built with the C# 5 compiler that ships inside Windows
// (C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe), so it runs on any
// Windows 10/11 box without .NET or any runtime installed. The application payload
// is a zip embedded as a manifest resource (see build.ps1).
//
// Switches:
//   /SILENT          install with defaults, no UI (exit code 0 = success)
//   /DIR=<path>      install destination (with /SILENT)
//   /UNINSTALL       remove the installation registered in the registry
//   /LANG=fa|en      force the setup UI's language instead of following Windows'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DnsPilotInstaller
{
    internal static class Program
    {
        public const string AppName = "DnsPilot";
        public const string Version = "1.0.0";
        public const string Publisher = "esi";
        public const string PayloadResource = "dnspilot.payload.zip";

        [STAThread]
        private static void Main(string[] args)
        {
            bool uninstall = false;
            bool silent = false;
            string dir = null;
            string lang = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "/UNINSTALL", StringComparison.OrdinalIgnoreCase)) uninstall = true;
                else if (string.Equals(a, "/SILENT", StringComparison.OrdinalIgnoreCase)) silent = true;
                else if (a.StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase)) dir = a.Substring(5).Trim('"');
                else if (string.Equals(a, "/DIR", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) dir = args[++i].Trim('"');
                else if (a.StartsWith("/LANG=", StringComparison.OrdinalIgnoreCase)) lang = a.Substring(6);
            }

            L.Init(lang);

            if (uninstall)
            {
                Environment.Exit(Uninstall.Run(silent));
                return;
            }

            if (silent)
            {
                Environment.Exit(Install.Run(dir ?? DefaultDir(), true, true, null));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(dir));
        }

        public static string DefaultDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        }
    }

    // ------------------------------------------------------------------ strings
    internal static class L
    {
        public static bool IsFa;
        private static readonly Dictionary<string, string[]> T = new Dictionary<string, string[]>();

        static L()
        {
            Add("title", "نصب DnsPilot", "DnsPilot Setup");
            // What the app does, in one line - the setup window is where a new user reads the
            // product's self-description, so it says "switch DNS" and nothing else. The old
            // wording ("network monitoring and control") belonged to a different product.
            Add("sub", "تعویض سریع سرور DNS ویندوز", "Switch your Windows DNS quickly");
            Add("creator", "سازنده", "Creator");
            Add("dest", "پوشهٔ نصب", "Install folder");
            Add("browse", "انتخاب…", "Browse…");
            Add("desktop", "میان‌بر روی دسکتاپ", "Desktop shortcut");
            Add("startmenu", "میان‌بر در منوی Start", "Start menu shortcut");
            Add("install", "نصب", "Install");
            Add("cancel", "انصراف", "Cancel");
            Add("finish", "پایان", "Finish");
            Add("ready", "برای شروع نصب، روی «نصب» بزنید.", "Click Install to begin.");
            Add("installing", "در حال نصب…", "Installing…");
            Add("extracting", "در حال استخراج فایل‌ها", "Extracting files");
            Add("done", "نصب با موفقیت انجام شد.", "Installation completed successfully.");
            Add("launch", "اجرای DnsPilot", "Launch DnsPilot");
            Add("close_app", "DnsPilot در حال اجراست و بسته می‌شود. ادامه می‌دهید؟",
                "DnsPilot is running and will be closed. Continue?");
            Add("app_desc", "تعویض سرور DNS ویندوز", "Switch your Windows DNS quickly");
            Add("uninstall_done", "DnsPilot از سیستم حذف شد.", "DnsPilot has been removed from this computer.");
            // Stated separately because it is a separate, verifiable action, not part of the
            // summary. Leaving the machine on a resolver chosen inside an app that has just been
            // uninstalled is the failure this avoids, so the message says which way it went.
            Add("uninstall_dns", "DNS هم به حالت خودکار بازگردانده شد.",
                "DNS was also returned to automatic.");
            Add("uninstall_dns_failed", "DNS به حالت خودکار برنگشت؛ پیش از اتصال، آن را بررسی کنید.",
                "DNS could not be returned to automatic; check it before going online.");
            Add("error", "خطا در نصب", "Setup error");
            Add("bad_dir", "محل نصب انتخاب نشده است.", "Please choose an install folder.");
        }

        private static void Add(string key, string fa, string en) { T[key] = new string[] { fa, en }; }

        /// <summary>
        /// Selects the label language from the Windows UI culture, or from /LANG=fa|en when the
        /// argument was given - an explicit switch beats the machine's preference, and it also
        /// gives the screenshot tools a way to capture both languages without touching the
        /// system's own language settings.
        /// </summary>
        public static void Init(string forced)
        {
            G("title");   // forces the static table to load
            string want = (forced ?? string.Empty).Trim().ToLowerInvariant();
            if (want.Length == 0) want = Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName;
            IsFa = want == "fa" || want == "fa-ir";
        }

        public static string G(string key)
        {
            string[] v;
            if (T.TryGetValue(key, out v)) return IsFa ? v[0] : v[1];
            return key;
        }
    }

    // ------------------------------------------------------------------ shortcuts
    internal static class Shortcut
    {
        /// <summary>Creates a .lnk through the in-box WScript.Shell COM object.</summary>
        public static void Create(string lnkPath, string target, string description)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) throw new InvalidOperationException("WScript.Shell unavailable");
            object sh = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnkPath });
                Type scType = sc.GetType();
                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc,
                    new object[] { Path.GetDirectoryName(target) });
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc);
            }
            finally
            {
                Marshal.FinalReleaseComObject(sh);
            }
        }

        public static void Delete(string lnkPath)
        {
            try { if (File.Exists(lnkPath)) File.Delete(lnkPath); }
            catch { /* already gone */ }
        }
    }

    // ------------------------------------------------------------------ install
    internal static class Install
    {
        /// <summary>Writes a line into the uninstall manifest (files + shortcuts we created).</summary>
        private class Manifest
        {
            public readonly List<string> Lines = new List<string>();
            public void AddFile(string relative) { if (!Lines.Contains("file:" + relative)) Lines.Add("file:" + relative); }
            public void AddLink(string full) { if (!Lines.Contains("shortcut:" + full)) Lines.Add("shortcut:" + full); }

            /// <summary>
            /// Keeps what a previous install recorded. An upgrade overwrites this manifest, and
            /// a file the old payload had that the new one no longer carries would otherwise
            /// drop out of the list and never be uninstalled - so old entries merge in first,
            /// and deduplication keeps a re-listed file single.
            /// </summary>
            public void Merge(string dir)
            {
                string path = Path.Combine(dir, "uninstall.dat");
                if (!File.Exists(path)) return;
                foreach (string line in System.IO.File.ReadAllLines(path))
                {
                    string t = line.Trim();
                    if (t.StartsWith("file:") || t.StartsWith("shortcut:"))
                        if (!Lines.Contains(t)) Lines.Add(t);
                }
            }

            public void Save(string dir) { System.IO.File.WriteAllText(Path.Combine(dir, "uninstall.dat"), string.Join(Environment.NewLine, Lines.ToArray())); }
        }

        public static int Run(string dest, bool desktop, bool menu, SetupForm ui)
        {
            try
            {
                if (string.IsNullOrEmpty(dest)) return 2;
                dest = dest.TrimEnd('\\');

                if (!CloseRunningApp(ui)) return 3;   // user declined to close the app
                Directory.CreateDirectory(dest);

                Manifest manifest = new Manifest();
                manifest.Merge(dest);

                ExtractPayload(dest, ui, manifest);

                string exe = Path.Combine(dest, Program.AppName + ".exe");
                if (!File.Exists(exe)) throw new FileNotFoundException("payload did not contain " + Program.AppName + ".exe");

                // Saved the moment the payload is on disk. A failure in anything after this
                // point - a COM shortcut API, the registry - must still leave a manifest that
                // describes the files that DID land, otherwise the machine ends up with an app
                // installed and no record to remove it by.
                manifest.Save(dest);

                // The uninstall registered in Programs and Features has to live inside the
                // installation itself: pointing it at the original setup.exe means an entry
                // that breaks the moment the user tidies their Downloads folder. The copy is
                // part of what gets uninstalled, so it goes into the manifest too.
                string uninstaller = CopySelf(dest);
                if (uninstaller != null)
                {
                    manifest.AddFile(uninstaller);
                    manifest.Save(dest);
                }

                // Shortcuts for every user (the installer is elevated).
                string commonDesktop = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Program.AppName + ".lnk");
                string commonMenu = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs");
                string menuLink = Path.Combine(commonMenu, Program.AppName + ".lnk");

                if (desktop)
                {
                    Shortcut.Create(commonDesktop, exe, L.G("app_desc"));
                    manifest.AddLink(commonDesktop);
                    manifest.Save(dest);
                }
                if (menu)
                {
                    Shortcut.Create(menuLink, exe, L.G("app_desc"));
                    manifest.AddLink(menuLink);
                    manifest.Save(dest);
                }

                RegisterUninstall(dest, exe, uninstaller);
                return 0;
            }
            catch (Exception ex)
            {
                if (ui != null) ui.ShowError(ex.Message);
                else Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        /// <summary>
        /// Copies this setup program itself into the install folder as the uninstaller. Returns
        /// the copy, or null when it could not be made - uninstall registration then falls back
        /// to the original exe, which is the old behaviour rather than a failure.
        /// </summary>
        private static string CopySelf(string dest)
        {
            try
            {
                string source = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(source)) return null;
                string target = Path.Combine(dest, "Uninstall.exe");
                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                    return target;   // already running from the copy itself: re-running the installer
                File.Copy(source, target, true);
                return target;
            }
            catch { return null; }
        }

        /// <summary>
    /// Puts every adapter's DNS back to automatic.
    /// </summary>
    /// <remarks>
    /// Changing that setting is this program's entire purpose, which makes it the only thing here
    /// with anything to undo. Uninstalling while leaving a machine on a static resolver the user
    /// picked inside the app is how someone ends up with a computer that resolves nothing and no
    /// program left to fix it - so this runs on every adapter rather than only the ones the app
    /// remembers, because the app's own record lives in its user profile and is gone either way.
    ///
    /// Best effort by design. An adapter on a static IP has no DHCP server to take DNS from and
    /// netsh says so; that is not worth failing an uninstall over. The return value reports
    /// whether anything actually changed, so the caller can state what happened rather than
    /// assume it.
    /// </remarks>
    internal static class DnsState
    {
        public static bool RestoreAutomatic()
        {
            bool changed = false;

            NetworkInterface[] adapters;
            try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return false; }

            foreach (NetworkInterface nic in adapters)
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (string.IsNullOrEmpty(nic.Name)) continue;

                // A quote in an adapter name is legal, but this argument string is built by
                // hand here (the app's own code path uses ProcessStartInfo.ArgumentList; a
                // program compiled by the in-box C# 5 compiler cannot). A quoted name
                // would close the quoting early and hand netsh a fragment instead of a name,
                // so such an adapter is skipped rather than mangled - netsh could not address
                // it through this spelling either way.
                if (nic.Name.IndexOf('"') >= 0) continue;

                // The name is quoted because adapter names routinely contain spaces, and netsh
                // wants the display name here rather than the interface GUID.
                if (RunNetsh("interface ip set dns name=\"" + nic.Name + "\" source=dhcp")) changed = true;
                if (RunNetsh("interface ipv6 set dnsservers name=\"" + nic.Name + "\" source=dhcp")) changed = true;
            }

            return changed;
        }

        private static bool RunNetsh(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.WaitForExit(8000);
                    bool ok = p.ExitCode == 0;
                    p.Dispose();
                    return ok;
                }
            }
            catch { return false; }
        }
    }

        private static bool CloseRunningApp(SetupForm ui)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(Program.AppName); }
            catch { return true; }
            if (procs.Length == 0) return true;

            if (ui != null && !ui.Confirm(L.G("close_app")))
            {
                foreach (Process p in procs) p.Dispose();
                return false;
            }
            foreach (Process p in procs)
            {
                try
                {
                    p.Kill();
                    // Waiting rather than sleeping: the process tells us when it has gone,
                    // which on a busy machine takes longer than 800 ms and on a quick one
                    // spends nothing. Three seconds is the ceiling; a killed but wedged
                    // process still gets overwritten by the payload extraction below.
                    p.WaitForExit(3000);
                }
                catch { /* already exiting */ }
                finally { p.Dispose(); }
            }
            return true;
        }

        private static void ExtractPayload(string dest, SetupForm ui, Manifest manifest)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            Stream raw = asm.GetManifestResourceStream(Program.PayloadResource);
            if (raw == null) throw new InvalidOperationException("embedded payload is missing");

            using (raw)
            using (ZipArchive zip = new ZipArchive(raw, ZipArchiveMode.Read))
            {
                List<ZipArchiveEntry> files = new List<ZipArchiveEntry>();
                foreach (ZipArchiveEntry e in zip.Entries)
                    if (e.FullName.Length > 0 && e.FullName[e.FullName.Length - 1] != '/')
                        files.Add(e);

                if (ui != null) ui.BeginProgress(files.Count);

                for (int i = 0; i < files.Count; i++)
                {
                    ZipArchiveEntry e = files[i];
                    string relative = e.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string target = Path.Combine(dest, relative);

                    // The zip is written by this repo's own build script, but a step that
                    // writes files is still one step away from being the step that trusts
                    // them: an entry spelled "..\.." would escape the install folder, and a
                    // poisoned payload would then place files wherever it liked. Resolving the
                    // final path and requiring it to stay inside dest costs one comparison.
                    if (!Path.GetFullPath(target).StartsWith(
                            Path.GetFullPath(dest) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                        throw new IOException("payload entry would escape the install folder: " + relative);

                    string parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    // Overwrite: this is also how an upgrade replaces a previous install.
                    e.ExtractToFile(target, true);
                    manifest.AddFile(relative);

                    if (ui != null) ui.ReportProgress(i + 1, files.Count, Path.GetFileName(relative));
                }
            }
        }

        private static void RegisterUninstall(string dest, string exe, string uninstaller)
        {
            try
            {
                using (var key = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                {
                    using (var sub = key.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Program.AppName))
                    {
                        if (sub == null) return;
                        sub.SetValue("DisplayName", Program.AppName);
                        sub.SetValue("DisplayVersion", Program.Version);
                        sub.SetValue("Publisher", Program.Publisher);
                        sub.SetValue("InstallLocation", dest);
                        sub.SetValue("DisplayIcon", exe);
                        sub.SetValue("UninstallString", "\"" + (uninstaller ?? Assembly.GetExecutingAssembly().Location) + "\" /UNINSTALL");
                        sub.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        sub.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        try
                        {
                            long kb = 0;
                            foreach (string f in Directory.GetFiles(dest, "*", SearchOption.AllDirectories))
                                kb += new FileInfo(f).Length;
                            sub.SetValue("EstimatedSize", (int)Math.Min(kb / 1024, int.MaxValue), Microsoft.Win32.RegistryValueKind.DWord);
                        }
                        catch { /* size is informational only */ }
                    }
                }
            }
            catch { /* programs-and-features entry is a convenience, not a requirement */ }
        }
    }

    // ------------------------------------------------------------------ uninstall
    internal static class Uninstall
    {
        public static int Run(bool silent)
        {
            try
            {
                string dest = ResolveInstallDir();
                if (string.IsNullOrEmpty(dest))
                {
                    if (!silent) MessageBox.Show(L.G("uninstall_done"), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Process[] procs = Process.GetProcessesByName(Program.AppName);
                foreach (Process p in procs)
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                    finally { p.Dispose(); }
                }

                bool dnsRestored = RemoveShortcutsAndFiles(dest);

                // The copy of this program inside the installation is also part of what gets
                // removed, and it cannot delete itself while it is running - File.Delete on a
                // running exe is refused by Windows. A separate cmd, told to wait and then
                // delete, exits the list: the uninstaller can return, and the file is gone a
                // moment later. The window-less cmd outlives this process by design.
                ScheduleSelfDelete(dest);

                // Registry entry (ours only).
                try
                {
                    using (var key = Microsoft.Win32.RegistryKey.OpenBaseKey(
                        Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                        key.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Program.AppName, false);
                }
                catch { }

                if (!silent)
                {
                    // Two facts, stated separately: the app is gone, and one of the two possible
                    // outcomes for DNS. Which of them the user is told is decided by what actually
                    // happened, not by what was meant to.
                    MessageBox.Show(
                        L.G("uninstall_done") + "\r\n\r\n" +
                        L.G(dnsRestored ? "uninstall_dns" : "uninstall_dns_failed"),
                        Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return 0;
            }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show(ex.Message, L.G("error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        /// <summary>
        /// Deletes the installed copy of this program after the uninstall process has exited,
        /// when this process is that copy. Anything else (install run from Downloads, say)
        /// schedules nothing - the uninstall evidence in that folder is not ours to clean.
        /// </summary>
        private static void ScheduleSelfDelete(string dest)
        {
            try
            {
                string self = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(self)) return;
                if (!self.StartsWith(Path.GetFullPath(dest) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;

                // The classic ping delay: two seconds of waiting that outlives this process,
                // after which the file is deletable. CreateNoWindow keeps the cmd invisible.
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    Arguments = "/c ping -n 3 127.0.0.1 > nul & del /f \"" + self + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(psi);
            }
            catch
            {
                // A copy left behind is a nuisance, not a failure; the uninstall itself is done.
            }
        }

        private static string ResolveInstallDir()
        {
            try
            {
                using (var key = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var sub = key.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Program.AppName))
                {
                    if (sub != null)
                    {
                        object v = sub.GetValue("InstallLocation");
                        if (v != null) return v.ToString().TrimEnd('\\');
                    }
                }
            }
            catch { }

            // Fallback: the payload directory the setup itself was unpacked from is not
            // the install dir, so without registry data there is nothing safe to remove.
            return null;
        }

        /// <returns>True when the DNS settings were returned to automatic, false when they were not.</returns>
        private static bool RemoveShortcutsAndFiles(string dest)
        {
            string manifestPath = Path.Combine(dest, "uninstall.dat");
            List<string> files = new List<string>();
            List<string> links = new List<string>();

            if (File.Exists(manifestPath))
            {
                foreach (string line in File.ReadAllLines(manifestPath))
                {
                    if (line.StartsWith("file:")) files.Add(line.Substring(5));
                    else if (line.StartsWith("shortcut:")) links.Add(line.Substring(9));
                }
            }

            foreach (string l in links) Shortcut.Delete(l);

            // Remove only what we installed, deepest paths first, then prune empty folders.
            files.Sort(delegate (string a, string b) { return b.Length.CompareTo(a.Length); });
            foreach (string rel in files)
            {
                try
                {
                    string full = Path.Combine(dest, rel);
                    if (File.Exists(full)) File.Delete(full);
                }
                catch { /* locked or already gone */ }
            }

            try { if (File.Exists(manifestPath)) File.Delete(manifestPath); }
            catch { }

            // The firewall block this replaced belonged to a different product: an inbound rule
            // for a phone bridge this app does not have. What this app does owe the user is the
            // setting it was installed to manage, so that is what gets undone, on every adapter
            // rather than only the ones it remembers.
            // DnsState lives beside the code that writes these settings in the first place, which
            // makes it a helper of Install rather than of Uninstall; the qualification is the
            // compiler making that nesting visible rather than anything needing to be decided.
            bool dnsRestored = Install.DnsState.RestoreAutomatic();

            PruneEmptyDirs(dest);

            return dnsRestored;
        }

        /// <summary>
        /// Removes the folders this install created. Deepest first, and only when a folder
        /// is completely empty, so nothing the user put in there is ever touched.
        /// </summary>
        private static void PruneEmptyDirs(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;

                // Nested folders need more than one pass: a child can only disappear
                // after its own children did.
                for (int pass = 0; pass < 6; pass++)
                {
                    string[] dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
                    Array.Sort(dirs, delegate (string a, string b) { return b.Length.CompareTo(a.Length); });

                    bool removed = false;
                    foreach (string d in dirs)
                    {
                        try
                        {
                            if (Directory.GetFileSystemEntries(d).Length == 0)
                            {
                                Directory.Delete(d);
                                removed = true;
                            }
                        }
                        catch { /* locked or protected: keep it */ }
                    }
                    if (!removed) break;
                }

                if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length == 0)
                    Directory.Delete(root);
            }
            catch { /* leave what we cannot safely remove */ }
        }
    }

    // ------------------------------------------------------------------ UI
    internal sealed class SetupForm : Form
    {
        private readonly TextBox _dest;
        private readonly CheckBox _desktop;
        private readonly CheckBox _menu;
        private readonly CheckBox _launch;
        private readonly Button _primary;
        private readonly Button _cancel;
        private readonly Label _hint;
        private readonly Label _task;
        private readonly ProgressBar _bar;
        private bool _installed;

        public SetupForm(string initialDir)
        {
            Text = L.G("title");
            ClientSize = new Size(474, 336);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 0);
            BackColor = Color.White;
            if (L.IsFa)
            {
                RightToLeft = RightToLeft.Yes;
                // RightToLeftLayout is deliberately NOT used: the Win32 LAYOUT_RTL style it sets
                // mirrors the whole drawing surface, and on Windows 11 that style turns every
                // label's glyphs into their own mirror image - "نصب" rendered as "بصن" - rather
                // than a proper RTL line. RightToLeft maps reading order, check-box position and
                // text alignment; the geometry itself is mirrored by hand further down, where
                // every location is known and nothing is left to a window style.
            }

            try
            {
                // The setup exe carries the app's own icon (build.ps1 passes /win32icon), so
                // title bar, taskbar and Programs-and-Features all show the same face.
                Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            }
            catch { /* icon is decoration; a failure keeps the default */ }

            // Accent header
            var header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 86;
            header.BackColor = Color.FromArgb(24, 28, 40);
            Controls.Add(header);

            // The app's icon at header size, extracted from the running exe. If that ever
            // fails, the fallback is a plain letter badge in the brand colour - the point of
            // the badge is to identify the product, not to depend on a resource.
            Control badge = null;
            try
            {
                using (Icon appIcon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location))
                {
                    if (appIcon != null)
                    {
                        var pic = new PictureBox();
                        pic.Image = appIcon.ToBitmap();
                        pic.Size = new Size(46, 46);
                        pic.SizeMode = PictureBoxSizeMode.Zoom;
                        pic.Location = new Point(24, 20);
                        pic.BackColor = header.BackColor;
                        badge = pic;
                    }
                }
            }
            catch { }
            if (badge == null)
            {
                var glyph = new Label();
                glyph.Text = "D";
                glyph.Font = new Font("Segoe UI Semibold", 22F, FontStyle.Bold);
                glyph.ForeColor = Color.White;
                glyph.BackColor = Color.FromArgb(79, 91, 213);   // DnsPilot brand indigo
                glyph.TextAlign = ContentAlignment.MiddleCenter;
                glyph.Size = new Size(46, 46);
                glyph.Location = new Point(24, 20);
                badge = glyph;
            }
            header.Controls.Add(badge);

            var title = new Label();
            title.Text = L.G("title");
            title.Font = new Font("Segoe UI", 15F, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.BackColor = Color.Transparent;
            title.AutoSize = true;
            title.Location = new Point(86, 22);
            header.Controls.Add(title);

            var sub = new Label();
            sub.Text = L.G("sub");
            sub.ForeColor = Color.FromArgb(150, 160, 180);
            sub.BackColor = Color.Transparent;
            sub.AutoSize = true;
            sub.Location = new Point(88, 54);
            header.Controls.Add(sub);

            var creator = new Label();
            creator.Text = L.G("creator") + ":  esi";
            creator.ForeColor = Color.FromArgb(124, 141, 245);   // the brand's light indigo
            creator.BackColor = Color.Transparent;
            creator.AutoSize = true;
            creator.Location = new Point(88, 71);
            header.Controls.Add(creator);

            var destLabel = new Label();
            destLabel.Text = L.G("dest");
            destLabel.AutoSize = true;
            destLabel.Location = new Point(24, 106);
            Controls.Add(destLabel);

            _dest = new TextBox();
            _dest.Location = new Point(24, 126);
            _dest.Size = new Size(344, 23);
            _dest.Text = initialDir ?? Program.DefaultDir();
            Controls.Add(_dest);

            var browse = new Button();
            browse.Text = L.G("browse");
            browse.Location = new Point(376, 125);
            browse.Size = new Size(74, 25);
            browse.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.SelectedPath = _dest.Text;
                    if (dlg.ShowDialog(this) == DialogResult.OK) _dest.Text = dlg.SelectedPath;
                }
            };
            Controls.Add(browse);

            _desktop = new CheckBox();
            _desktop.Text = L.G("desktop");
            _desktop.Checked = true;
            _desktop.AutoSize = true;
            _desktop.Location = new Point(24, 162);
            Controls.Add(_desktop);

            _menu = new CheckBox();
            _menu.Text = L.G("startmenu");
            _menu.Checked = true;
            _menu.AutoSize = true;
            _menu.Location = new Point(24, 186);
            Controls.Add(_menu);

            _task = new Label();
            _task.Text = L.G("ready");
            _task.AutoSize = false;
            _task.Location = new Point(24, 222);
            _task.Size = new Size(426, 20);
            _task.ForeColor = Color.FromArgb(100, 116, 139);
            Controls.Add(_task);

            _bar = new ProgressBar();
            _bar.Location = new Point(24, 248);
            _bar.Size = new Size(426, 8);
            _bar.Visible = false;
            Controls.Add(_bar);

            _hint = new Label();
            _hint.AutoSize = false;
            _hint.Location = new Point(24, 262);
            _hint.Size = new Size(426, 18);
            _hint.ForeColor = Color.FromArgb(148, 163, 184);
            _hint.Font = new Font("Segoe UI", 8F);
            Controls.Add(_hint);

            _launch = new CheckBox();
            _launch.Text = L.G("launch");
            _launch.Checked = true;
            _launch.AutoSize = true;
            _launch.Location = new Point(24, 286);
            _launch.Visible = false;
            Controls.Add(_launch);

            _cancel = new Button();
            _cancel.Text = L.G("cancel");
            _cancel.Size = new Size(92, 30);
            _cancel.Location = new Point(358, 290);
            _cancel.Click += delegate { Close(); };
            Controls.Add(_cancel);

            _primary = new Button();
            _primary.Text = L.G("install");
            _primary.Size = new Size(92, 30);
            _primary.Location = new Point(258, 290);
            _primary.Click += delegate { OnPrimary(); };
            _primary.BackColor = Color.FromArgb(79, 91, 213);    // DnsPilot brand indigo
            _primary.ForeColor = Color.White;
            _primary.FlatStyle = FlatStyle.Flat;
            _primary.FlatAppearance.BorderSize = 0;
            _primary.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            Controls.Add(_primary);

            AcceptButton = _primary;
            CancelButton = _cancel;

            if (L.IsFa)
            {
                // The hand-mirror promised above. Runs once, after every control has been added
                // and auto-sized: AutoSize labels and check boxes have a final Width by now, so
                // flipping x around the right edge reproduces exactly the geometry the English
                // form has, seen from the other side. Fixed-size boxes give the same numbers;
                // nothing depends on a window style or a paint path here.
                int w = ClientSize.Width;   // 474 - both the header panel (Dock=Top) and this
                foreach (Control c in new Control[] { badge, title, sub, creator })
                    c.Location = new Point(header.Width - c.Location.X - c.Width, c.Location.Y);
                foreach (Control c in new Control[]
                         { destLabel, _dest, browse, _desktop, _menu,
                           _task, _bar, _hint, _launch, _cancel, _primary })
                    c.Location = new Point(w - c.Location.X - c.Width, c.Location.Y);
            }
        }

        private void OnPrimary()
        {
            if (_installed)
            {
                if (_launch.Checked)
                {
                    try
                    {
                        string exe = Path.Combine(_dest.Text.TrimEnd('\\'), Program.AppName + ".exe");
                        if (File.Exists(exe))
                        {
                            var psi = new ProcessStartInfo(exe);
                            Process.Start(psi);
                        }
                    }
                    catch { /* launching is optional */ }
                }
                Close();
                return;
            }

            string dest = _dest.Text.Trim();
            if (dest.Length == 0) { MessageBox.Show(this, L.G("bad_dir"), L.G("error"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            _dest.Enabled = false;
            _desktop.Enabled = false;
            _menu.Enabled = false;
            _cancel.Enabled = false;
            _primary.Enabled = false;
            _task.Text = L.G("installing");

            string d = dest;
            bool desk = _desktop.Checked;
            bool sm = _menu.Checked;

            var th = new Thread(delegate()
            {
                int rc = Install.Run(d, desk, sm, this);
                try
                {
                    BeginInvoke(new Action<int>(Finish), rc);
                }
                catch { /* form already closed */ }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void Finish(int rc)
        {
            // Every outcome re-enables the form first; rc decides what the task line says next.
            _dest.Enabled = true;
            _desktop.Enabled = true;
            _menu.Enabled = true;
            _cancel.Enabled = true;
            _primary.Enabled = true;
            _bar.Visible = false;

            // 3 = the user declined to close the running app; nothing failed, so the form goes
            // back to its ready state without an error message.
            if (rc == 3) { _task.Text = L.G("ready"); return; }

            if (rc != 0)
            {
                _task.Text = L.G("error");
                return;
            }

            _installed = true;
            _task.Text = L.G("done");
            _hint.Text = "";
            _launch.Visible = true;
            _primary.Text = L.G("finish");
            _cancel.Enabled = false;
        }

        // Called from the worker thread.
        public void BeginProgress(int total)
        {
            RunOnUi(delegate
            {
                _bar.Visible = true;
                _bar.Minimum = 0;
                _bar.Maximum = Math.Max(total, 1);
                _bar.Value = 0;
            });
        }

        public void ReportProgress(int done, int total, string name)
        {
            RunOnUi(delegate
            {
                if (done <= _bar.Maximum) _bar.Value = done;
                _task.Text = L.G("extracting") + "  (" + done + " / " + total + ")";
                _hint.Text = name;
            });
        }

        public bool Confirm(string message)
        {
            if (InvokeRequired)
            {
                bool ok = false;
                Invoke((Action)delegate
                {
                    ok = MessageBox.Show(this, message, Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                });
                return ok;
            }
            return MessageBox.Show(this, message, Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        public void ShowError(string message)
        {
            RunOnUi(delegate
            {
                MessageBox.Show(this, message, L.G("error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            });
        }

        private void RunOnUi(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(action); }
                catch { /* form already closed */ }
                return;
            }
            action();
        }
    }
}
