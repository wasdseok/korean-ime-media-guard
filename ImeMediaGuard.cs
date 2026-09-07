using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ImeMediaGuard
{
    // Pure policy: never changes or synthesizes a key. DWORD event-time differences
    // use unchecked subtraction, so the 150ms window survives a tick-count wrap.
    public sealed class SuppressionPolicy
    {
        public const uint T = 0x54;
        public const uint O = 0x4F;
        public const uint Hangul = 0x15;
        public const uint MediaNext = 0xB0;
        public const uint WindowMs = 150;
        private bool hasT, hasO, hasHangul, blockedMediaDown, passedMediaDown;
        private uint tTime, oTime, hangulTime;
        public bool Paused { get; private set; }

        public void SetPaused(bool paused)
        {
            Paused = paused;
            hasT = hasO = hasHangul = false;
            // Keep ownership of a suppressed down until its matching up arrives.
            // Passing this up through would create an unmatched key-up downstream.
        }

        public long DeltaT(uint time) { return hasT ? (long)unchecked(time - tTime) : -1L; }
        public long DeltaO(uint time) { return hasO ? (long)unchecked(time - oTime) : -1L; }
        public long DeltaHangul(uint time) { return hasHangul ? (long)unchecked(time - hangulTime) : -1L; }

        public bool Suppress(int hookCode, uint vk, bool isDown, uint time, uint scan, uint flags)
        {
            if (hookCode < 0) return false;
            if (vk != MediaNext)
            {
                if (!Paused && !isDown)
                {
                    if (vk == T) { hasT = true; tTime = time; }
                    else if (vk == O) { hasO = true; oTime = time; }
                    else if (vk == Hangul) { hasHangul = true; hangulTime = time; }
                }
                return false;
            }
            // The observed rogue events have zero scan code and LLKHF_INJECTED.
            // Keep unrelated hardware/media input independent of this pair state:
            // it must pass even while a matching injected press is suppressed.
            if (scan != 0 || (flags & 0x10) == 0) return false;
            if (blockedMediaDown)
            {
                if (!isDown) blockedMediaDown = false;
                return true;
            }
            if (passedMediaDown)
            {
                if (!isDown) passedMediaDown = false;
                return false;
            }
            if (!isDown) return false;
            if (Paused) { passedMediaDown = true; return false; }
            bool afterT = hasT && unchecked(time - tTime) <= WindowMs;
            bool afterO = hasO && unchecked(time - oTime) <= WindowMs;
            bool afterHangul = hasHangul && unchecked(time - hangulTime) <= WindowMs;
            if (!afterT && !afterO && !afterHangul) { passedMediaDown = true; return false; }
            blockedMediaDown = true;
            return true;
        }
    }

    public sealed class MediaEvent
    {
        public uint vk { get; set; }
        public uint scan { get; set; }
        public uint flags { get; set; }
        public uint time { get; set; }
        public string kind { get; set; }
        public long deltaSinceTKeyUpMs { get; set; }
        public long deltaSinceOKeyUpMs { get; set; }
        public long deltaSinceHangulKeyUpMs { get; set; }
        public bool blocked { get; set; }
        public bool paused { get; set; }
    }

    internal sealed class GuardContext : ApplicationContext
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        private static HookProc rootedCallback;
        private static GuardContext current;
        private readonly SuppressionPolicy policy = new SuppressionPolicy();
        private readonly MediaEvent[] events = new MediaEvent[256];
        private int eventCount, nextEvent;
        private long mediaEventCount, suppressedEventCount, suppressedPressCount;
        private IntPtr hook;
        private readonly NotifyIcon tray;
        private readonly ToolStripMenuItem stateItem, pauseItem;
        private readonly System.Windows.Forms.Timer timer;
        private readonly string statusPath;
        private string lastError;
        private readonly Stopwatch statusClock = Stopwatch.StartNew();
        private long savedMediaEventCount = -1, savedStatusAtMs;
        private string savedState, statusWriteError;
        private bool stopping;

        public GuardContext(string statusPath)
        {
            this.statusPath = statusPath;
            current = this;
            rootedCallback = KeyboardCallback;
            ContextMenuStrip menu = new ContextMenuStrip();
            stateItem = new ToolStripMenuItem("작동 중 · 차단 0회") { Enabled = false };
            pauseItem = new ToolStripMenuItem("일시 중지");
            pauseItem.Click += delegate { policy.SetPaused(!policy.Paused); RefreshUi(); FlushStatus(false); };
            ToolStripMenuItem quit = new ToolStripMenuItem("종료");
            quit.Click += delegate { ExitThread(); };
            menu.Items.Add(stateItem);
            menu.Items.Add(pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quit);
            tray = new NotifyIcon { Icon = SystemIcons.Information, ContextMenuStrip = menu,
                Text = "한글 입력 임시 보정 · 작동 중", Visible = true };
            timer = new System.Windows.Forms.Timer { Interval = 2000 };
            timer.Tick += delegate { RefreshUi(); FlushStatus(false); };

            // Use the current executable module, not a file path chosen externally.
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, rootedCallback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                lastError = "SetWindowsHookEx failed: " + Marshal.GetLastWin32Error();
                FlushStatus(false);
                tray.Visible = false;
                tray.Dispose();
                timer.Dispose();
                throw new InvalidOperationException(lastError);
            }
            timer.Start();
            FlushStatus(false);
        }

        private static IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            // No marshalling or state access when the hook chain requires forwarding.
            if (code < 0) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            int message = unchecked((int)wParam.ToInt64());
            bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
            bool up = message == WM_KEYUP || message == WM_SYSKEYUP;
            if (!down && !up) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            Kbd data = (Kbd)Marshal.PtrToStructure(lParam, typeof(Kbd));
            GuardContext ctx = current;
            if (ctx == null) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            bool suppressed = ctx.policy.Suppress(code, data.vkCode, down, data.time, data.scanCode, data.flags);
            if (data.vkCode == SuppressionPolicy.MediaNext)
            {
                // Bounded in-memory media-only log; all disk and UI work is on timer.
                ctx.events[ctx.nextEvent] = new MediaEvent { vk = data.vkCode, scan = data.scanCode,
                    flags = data.flags, time = data.time, kind = down ? "keydown" : "keyup",
                    deltaSinceTKeyUpMs = ctx.policy.DeltaT(data.time),
                    deltaSinceOKeyUpMs = ctx.policy.DeltaO(data.time),
                    deltaSinceHangulKeyUpMs = ctx.policy.DeltaHangul(data.time),
                    blocked = suppressed, paused = ctx.policy.Paused };
                ctx.nextEvent = (ctx.nextEvent + 1) % ctx.events.Length;
                if (ctx.eventCount < ctx.events.Length) ctx.eventCount++;
                ctx.mediaEventCount++;
                if (suppressed) { ctx.suppressedEventCount++; if (up) ctx.suppressedPressCount++; }
            }
            return suppressed ? new IntPtr(1) : CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private void RefreshUi()
        {
            string state = policy.Paused ? "일시 중지" : "작동 중";
            stateItem.Text = state + " · 차단 " + suppressedPressCount + "회 (이벤트 " + suppressedEventCount + "개)";
            pauseItem.Text = policy.Paused ? "다시 시작" : "일시 중지";
            tray.Text = "한글 입력 임시 보정 · " + state + " · " + suppressedPressCount + "회";
        }

        private void FlushStatus(bool stopped)
        {
            string currentState = stopped ? "stopped" : hook == IntPtr.Zero ? "error" : policy.Paused ? "paused" : "running";
            // Keep the two-second UI/event cadence, but an unchanged healthy status
            // needs only a 60-second heartbeat. Failed writes never update the cache.
            if (savedState == currentState && savedMediaEventCount == mediaEventCount &&
                lastError == null && statusWriteError == null &&
                statusClock.ElapsedMilliseconds - savedStatusAtMs < 60000) return;
            try
            {
                List<MediaEvent> recent = new List<MediaEvent>(eventCount);
                int first = (nextEvent - eventCount + events.Length) % events.Length;
                for (int i = 0; i < eventCount; i++) recent.Add(events[(first + i) % events.Length]);
                var status = new { schemaVersion = 2, utility = "ImeMediaGuard", policyVersion = "T-O-Hangul-injected-scan0-v2", pid = Process.GetCurrentProcess().Id,
                    state = currentState,
                    hookInstalled = hook != IntPtr.Zero, windowMs = SuppressionPolicy.WindowMs,
                    mediaEligibility = "vk=0xB0, scan=0, flags&0x10!=0",
                    mediaEventCount = mediaEventCount, blockedEventCount = suppressedEventCount,
                    blockedCompletedPressCount = suppressedPressCount, error = lastError,
                    updatedUtc = DateTime.UtcNow.ToString("o"), lastMediaEvents = recent };
                string directory = Path.GetDirectoryName(statusPath);
                Directory.CreateDirectory(directory);
                string temporary = statusPath + ".tmp";
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(status), new UTF8Encoding(false));
                if (File.Exists(statusPath)) File.Replace(temporary, statusPath, null);
                else File.Move(temporary, statusPath);
                savedState = currentState;
                savedMediaEventCount = mediaEventCount;
                savedStatusAtMs = statusClock.ElapsedMilliseconds;
                statusWriteError = null;
            }
            catch (Exception ex) { statusWriteError = ex.GetType().Name + ": " + ex.Message; }
        }

        protected override void ExitThreadCore()
        {
            if (stopping) return;
            stopping = true;
            timer.Stop();
            if (hook != IntPtr.Zero)
            {
                if (!UnhookWindowsHookEx(hook)) lastError = "UnhookWindowsHookEx failed: " + Marshal.GetLastWin32Error();
                else hook = IntPtr.Zero;
            }
            FlushStatus(true);
            tray.Visible = false;
            tray.Dispose();
            timer.Dispose();
            current = null;
            base.ExitThreadCore();
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct Kbd
        {
            public uint vkCode, scanCode, flags, time;
            public UIntPtr extraInfo;
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr GetModuleHandle(string moduleName);
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test")
                return RunTests(args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-results.json"));
            if (args.Length > 0 && args[0] != "--status-path") return 2;
            string statusPath = args.Length == 2 && args[0] == "--status-path" ? Path.GetFullPath(args[1]) :
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "work", "ime-diagnostics", "media-guard-status.json"));
            bool created;
            using (Mutex single = new Mutex(true, "Local\\CodexImeMediaGuard_" + Process.GetCurrentProcess().SessionId, out created))
            {
                if (!created) return 3;
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new GuardContext(statusPath));
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("임시 보정 프로그램을 시작하지 못했습니다.\n" + ex.Message, "한글 입력 임시 보정", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally { single.ReleaseMutex(); }
            }
        }

        private static int RunTests(string reportPath)
        {
            List<object> results = new List<object>();
            int failures = 0;
            Action<string, Action> test = delegate(string name, Action body) {
                try { body(); results.Add(new { name = name, passed = true, detail = "" }); }
                catch (Exception ex) { failures++; results.Add(new { name = name, passed = false, detail = ex.Message }); }
            };
            test("T keyup plus 5ms blocks media down and matching up", delegate {
                var p = new SuppressionPolicy(); Check(!p.Suppress(0, 0x54, false, 100, 0, 0x10));
                Check(p.Suppress(0, 0xB0, true, 105, 0, 0x10)); Check(p.Suppress(0, 0xB0, false, 110, 0, 0x10));
            });
            test("All non-media virtual keys always pass unchanged", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x54, false, 100, 0, 0x10);
                for (uint vk = 0; vk <= 255; vk++) if (vk != 0xB0) { Check(!p.Suppress(0, vk, true, 105, 0, 0x10)); Check(!p.Suppress(0, vk, false, 106, 0, 0x10)); }
            });
            test("Media next without trigger passes", delegate {
                var p = new SuppressionPolicy(); Check(!p.Suppress(0, 0xB0, true, 100, 0, 0x10)); Check(!p.Suppress(0, 0xB0, false, 105, 0, 0x10));
            });
            test("Media next after 151ms passes", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x54, false, 100, 0, 0x10);
                Check(!p.Suppress(0, 0xB0, true, 251, 0, 0x10)); Check(!p.Suppress(0, 0xB0, false, 260, 0, 0x10));
            });
            test("150ms window boundary included", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x54, false, 100, 0, 0x10); Check(p.Suppress(0, 0xB0, true, 250, 0, 0x10));
            });
            test("T keydown alone does not arm suppression", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x54, true, 100, 0, 0x10); Check(!p.Suppress(0, 0xB0, true, 105, 0, 0x10));
            });
            test("Late keyup and repeated blocked keydowns stay suppressed", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x54, false, 100, 0, 0x10);
                Check(p.Suppress(0, 0xB0, true, 105, 0, 0x10)); Check(p.Suppress(0, 0xB0, true, 500, 0, 0x10));
                Check(p.Suppress(0, 0xB0, true, 700, 0, 0x10)); Check(p.Suppress(0, 0xB0, false, 900, 0, 0x10));
                Check(!p.Suppress(0, 0xB0, true, 1000, 0, 0x10)); Check(!p.Suppress(0, 0xB0, false, 1010, 0, 0x10));
            });
            test("DWORD wrap keeps short interval accurate", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x54, false, uint.MaxValue - 4, 0, 0x10);
                Check(p.DeltaT(2) == 7); Check(p.Suppress(0, 0xB0, true, 2, 0, 0x10));
            });
            test("DWORD wrap rejects interval over threshold", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x54, false, uint.MaxValue - 100, 0, 0x10);
                Check(!p.Suppress(0, 0xB0, true, 100, 0, 0x10));
            });
            test("Hangul keyup plus 10ms blocks media", delegate {
                var p = new SuppressionPolicy(); Check(!p.Suppress(0, 0x15, false, 100, 0, 0x10)); Check(p.Suppress(0, 0xB0, true, 110, 0, 0x10));
            });
            test("Negative hook code always passes and changes no state", delegate {
                var p = new SuppressionPolicy(); Check(!p.Suppress(-1, 0x54, false, 100, 0, 0x10)); Check(!p.Suppress(0, 0xB0, true, 105, 0, 0x10));
                Check(!p.Suppress(0, 0xB0, false, 110, 0, 0x10));
                p.Suppress(0, 0x54, false, 200, 0, 0x10); Check(!p.Suppress(-1, 0xB0, true, 205, 0, 0x10));
                Check(!p.Suppress(0, 0xB0, false, 210, 0, 0x10)); Check(p.Suppress(0, 0xB0, true, 220, 0, 0x10));
                Check(!p.Suppress(-1, 0xB0, false, 230, 0, 0x10)); Check(p.Suppress(0, 0xB0, false, 240, 0, 0x10));
            });
            test("Pause passes new media but completes already blocked pair", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x54, false, 100, 0, 0x10); p.Suppress(0, 0xB0, true, 105, 0, 0x10);
                p.SetPaused(true); Check(p.Suppress(0, 0xB0, true, 200, 0, 0x10)); Check(p.Suppress(0, 0xB0, false, 300, 0, 0x10));
                Check(!p.Suppress(0, 0xB0, true, 310, 0, 0x10)); Check(!p.Suppress(0, 0xB0, false, 320, 0, 0x10));
            });
            test("Resume never reuses trigger recorded before or during pause", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x54, false, 100, 0, 0x10); p.SetPaused(true);
                p.Suppress(0, 0x54, false, 110, 0, 0x10); p.SetPaused(false); Check(!p.Suppress(0, 0xB0, true, 120, 0, 0x10));
                Check(!p.Suppress(0, 0xB0, false, 125, 0, 0x10));
                p.Suppress(0, 0x54, false, 130, 0, 0x10); Check(p.Suppress(0, 0xB0, true, 135, 0, 0x10));
            });
            test("Unpaired media keyup is never newly suppressed", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x54, false, 100, 0, 0x10); Check(!p.Suppress(0, 0xB0, false, 105, 0, 0x10));
            });
            test("Forwarded media press stays forwarded even across a new T trigger", delegate {
                var p = new SuppressionPolicy(); Check(!p.Suppress(0, 0xB0, true, 100, 0, 0x10));
                p.Suppress(0, 0x54, false, 110, 0, 0x10); Check(!p.Suppress(0, 0xB0, true, 115, 0, 0x10));
                Check(!p.Suppress(0, 0xB0, false, 120, 0, 0x10)); Check(p.Suppress(0, 0xB0, true, 130, 0, 0x10));
            });
            test("Media press started paused stays forwarded after resume", delegate {
                var p = new SuppressionPolicy(); p.SetPaused(true); Check(!p.Suppress(0, 0xB0, true, 100, 0, 0x10));
                p.SetPaused(false); p.Suppress(0, 0x54, false, 110, 0, 0x10);
                Check(!p.Suppress(0, 0xB0, true, 115, 0, 0x10)); Check(!p.Suppress(0, 0xB0, false, 120, 0, 0x10));
                Check(p.Suppress(0, 0xB0, true, 125, 0, 0x10));
            });
            test("O keyup plus 1ms blocks observed injected scan-zero event pair", delegate {
                var p = new SuppressionPolicy(); Check(!p.Suppress(0, 0x4F, false, 100, 24, 128));
                Check(p.DeltaO(101) == 1); Check(p.Suppress(0, 0xB0, true, 101, 0, 17));
                Check(p.Suppress(0, 0xB0, false, 200, 0, 145));
            });
            test("O keydown alone never arms suppression", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x4F, true, 100, 24, 0);
                Check(!p.Suppress(0, 0xB0, true, 101, 0, 17));
            });
            test("O 150ms boundary blocks but 151ms passes", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x4F, false, 100, 24, 128);
                Check(p.Suppress(0, 0xB0, true, 250, 0, 17)); Check(p.Suppress(0, 0xB0, false, 250, 0, 145));
                Check(!p.Suppress(0, 0xB0, true, 251, 0, 17)); Check(!p.Suppress(0, 0xB0, false, 252, 0, 145));
            });
            test("Non-injected media scan-zero events always pass", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x4F, false, 100, 24, 128);
                Check(!p.Suppress(0, 0xB0, true, 101, 0, 1)); Check(!p.Suppress(0, 0xB0, false, 102, 0, 129));
                Check(p.Suppress(0, 0xB0, true, 103, 0, 17));
                Check(!p.Suppress(0, 0xB0, true, 104, 0, 1)); Check(!p.Suppress(0, 0xB0, false, 105, 0, 129));
                Check(p.Suppress(0, 0xB0, false, 300, 0, 145));
            });
            test("Injected nonzero-scan events pass without ending blocked pair", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x54, false, 100, 20, 128);
                Check(!p.Suppress(0, 0xB0, true, 101, 25, 17)); Check(!p.Suppress(0, 0xB0, false, 102, 25, 145));
                Check(p.Suppress(0, 0xB0, true, 103, 0, 17));
                Check(!p.Suppress(0, 0xB0, true, 104, 25, 17)); Check(!p.Suppress(0, 0xB0, false, 105, 25, 145));
                Check(p.Suppress(0, 0xB0, true, 400, 0, 17)); Check(p.Suppress(0, 0xB0, false, 401, 0, 145));
            });
            test("Eligible forwarded pair remains forwarded across O trigger and other media", delegate {
                var p = new SuppressionPolicy(); Check(!p.Suppress(0, 0xB0, true, 90, 0, 17));
                p.Suppress(0, 0x4F, false, 100, 24, 128); Check(!p.Suppress(0, 0xB0, true, 101, 0, 17));
                Check(!p.Suppress(0, 0xB0, false, 102, 25, 145)); Check(!p.Suppress(0, 0xB0, true, 103, 0, 17));
                Check(!p.Suppress(0, 0xB0, false, 104, 0, 145)); Check(p.Suppress(0, 0xB0, true, 105, 0, 17));
            });
            test("Pause and resume clear O trigger including O pressed during pause", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x4F, false, 100, 24, 128); p.SetPaused(true);
                p.Suppress(0, 0x4F, false, 110, 24, 128); p.SetPaused(false); Check(p.DeltaO(115) == -1);
                Check(!p.Suppress(0, 0xB0, true, 120, 0, 17)); Check(!p.Suppress(0, 0xB0, false, 121, 0, 145));
            });
            test("Negative hook code cannot arm O or disturb eligible pair", delegate {
                var p = new SuppressionPolicy(); Check(!p.Suppress(-1, 0x4F, false, 100, 24, 128));
                Check(p.DeltaO(101) == -1); Check(!p.Suppress(0, 0xB0, true, 102, 0, 17));
                Check(!p.Suppress(0, 0xB0, false, 103, 0, 145)); p.Suppress(0, 0x4F, false, 110, 24, 128);
                Check(!p.Suppress(-1, 0xB0, true, 111, 0, 17)); Check(p.Suppress(0, 0xB0, true, 112, 0, 17));
                Check(!p.Suppress(-1, 0xB0, false, 113, 0, 145)); Check(p.Suppress(0, 0xB0, false, 300, 0, 145));
            });
            test("Unrelated Q keyup does not arm media suppression", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x51, false, 100, 16, 128);
                Check(!p.Suppress(0, 0xB0, true, 105, 0, 17)); Check(!p.Suppress(0, 0xB0, false, 110, 0, 145));
            });
            var report = new { passed = failures == 0, total = results.Count, failures = failures,
                testedUtc = DateTime.UtcNow.ToString("o"), globalHookInstalled = false, simulatedKeys = false, tests = results };
            File.WriteAllText(Path.GetFullPath(reportPath), new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
            return failures == 0 ? 0 : 1;
        }

        private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
    }
}
