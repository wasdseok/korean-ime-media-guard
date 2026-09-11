using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("TypingTune")]
[assembly: System.Reflection.AssemblyProduct("타이핑튠 (TypingTune)")]
[assembly: System.Reflection.AssemblyVersion("2.0.4.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.4.0")]

namespace TypingTune
{
    public sealed class UserSettings
    {
        public int SchemaVersion = 1;
        public GuardProfile Profile = GuardProfile.Default();
        public string KeyboardLabel = "노트북 내장 키보드";
        public string LayoutLabel = "한국어 두벌식";
    }

    internal static class AppStorage
    {
        public static string DataDirectory;
        public static JavaScriptSerializer Serializer()
        { return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 100 }; }
        public static void Save(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, Serializer().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        public static UserSettings LoadSettings()
        {
            try
            {
                string p = Path.Combine(DataDirectory, "settings.json");
                if (File.Exists(p)) return Serializer().Deserialize<UserSettings>(File.ReadAllText(p)) ?? new UserSettings();
            }
            catch { }
            return new UserSettings();
        }
    }

    internal static class Program
    {
        public const string Version = "2.0.4";
        internal static EventWaitHandle ShowEvent, ExitEvent;
        internal static string EventName(string action, string exePath)
        {
            string suffix = "";
            if (exePath != null)
            {
                using (SHA256 sha = SHA256.Create())
                    suffix = "_" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(exePath).ToLowerInvariant()))).Replace("-", "").Substring(0, 20);
            }
            return "Local\\TypingTune_" + action + "_" + Process.GetCurrentProcess().SessionId + suffix;
        }
        private static bool Signal(string action, string path)
        {
            try { using (EventWaitHandle e = EventWaitHandle.OpenExisting(EventName(action, path))) e.Set(); return true; }
            catch (WaitHandleCannotBeOpenedException) { return false; }
        }
        private static string Value(string[] args, string name)
        { for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1]; return null; }
        private static bool Has(string[] args, string name) { return Array.IndexOf(args, name) >= 0; }

        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppStorage.DataDirectory = Path.GetFullPath(Value(args, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypingTune"));
            if (Has(args, "--exit") || Has(args, "--exit-if-path"))
            { Signal("Exit", Value(args, "--exit-if-path") ?? Application.ExecutablePath); return 0; }
            string waitPid = Value(args, "--wait-for-exit");
            if (waitPid != null)
            {
                int id;
                if (!Int32.TryParse(waitPid, out id)) return 2;
                try { using (Process old = Process.GetProcessById(id)) if (!old.WaitForExit(15000)) return 4; }
                catch (ArgumentException) { }
            }
            if (Has(args, "--self-test"))
            {
                string reportPath = Value(args, "--self-test") ?? Path.Combine(AppStorage.DataDirectory, "self-test.json");
                try
                {
                    var result = new Dictionary<string, object>();
                    result["appVersion"] = Version; result["utc"] = DateTime.UtcNow.ToString("o");
                    Dictionary<string, object> core = CoreTests.Run(), diagnostics = DiagnosticTests.Run();
                    result["core"] = core; result["diagnostics"] = diagnostics;
                    AppStorage.Save(Path.GetFullPath(reportPath), result);
                    return Convert.ToInt32(core["failed"]) == 0 && Convert.ToInt32(diagnostics["failed"]) == 0 ? 0 : 1;
                }
                catch (Exception ex) { AppStorage.Save(Path.GetFullPath(reportPath), new { error = ex.ToString() }); return 1; }
            }
            bool ownsMutex;
            using (Mutex single = new Mutex(true, "Local\\TypingTune_" + Process.GetCurrentProcess().SessionId, out ownsMutex))
            {
                if (!ownsMutex) { for (int n = 0; n < 12; n++) { if (Signal("Show", null)) return 0; Thread.Sleep(100); } return 3; }
                try
                {
                    Directory.CreateDirectory(AppStorage.DataDirectory);
                    ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName("Show", null));
                    ExitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName("Exit", Application.ExecutablePath));
                    Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) {
                        AppStorage.Save(Path.Combine(AppStorage.DataDirectory, "last-error.json"), new { utc = DateTime.UtcNow.ToString("o"), error = e.Exception.ToString() });
                        MessageBox.Show("작업을 완료하지 못했습니다.\n" + e.Exception.Message, "TypingTune", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    };
                    using (MainForm form = new MainForm(Has(args, "--tray"))) Application.Run(form);
                    return 0;
                }
                catch (Exception ex) { MessageBox.Show("TypingTune을 시작하지 못했습니다.\n" + ex.Message, "TypingTune", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
                finally
                {
                    if (ShowEvent != null) ShowEvent.Dispose(); if (ExitEvent != null) ExitEvent.Dispose(); single.ReleaseMutex();
                }
            }
        }
    }
}
