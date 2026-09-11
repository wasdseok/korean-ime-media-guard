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
        public const uint D = 0x44;
        public const uint Y = 0x59;
        public const uint Q = 0x51;
        public const uint Backspace = 0x08;
        public const uint Hangul = 0x15;
        public const uint MediaNext = 0xB0;
        public const uint WindowMs = 150;
        public const uint CandidateDownWindowMs = 30;
        public const string Version = "T-O-D-Y-up-Q-Backspace-down-Hangul-injected-scan0-v4";
        private bool hasT, hasO, hasD, hasY, hasQDown, hasBackspaceDown, hasHangul, blockedMediaDown, passedMediaDown;
        private bool qHeld, backspaceHeld;
        private uint tTime, oTime, dTime, yTime, qDownTime, backspaceDownTime, hangulTime;
        public bool Paused { get; private set; }

        public void SetPaused(bool paused)
        {
            Paused = paused;
            hasT = hasO = hasD = hasY = hasQDown = hasBackspaceDown = hasHangul = false;
            // Keep held state across pause: an autorepeat after resume is not a new press.
            // Keep ownership of a suppressed down until its matching up arrives.
            // Passing this up through would create an unmatched key-up downstream.
        }

        public long DeltaT(uint time) { return hasT ? (long)unchecked(time - tTime) : -1L; }
        public long DeltaO(uint time) { return hasO ? (long)unchecked(time - oTime) : -1L; }
        public long DeltaD(uint time) { return hasD ? (long)unchecked(time - dTime) : -1L; }
        public long DeltaY(uint time) { return hasY ? (long)unchecked(time - yTime) : -1L; }
        public long DeltaQDown(uint time) { return hasQDown ? (long)unchecked(time - qDownTime) : -1L; }
        public long DeltaBackspaceDown(uint time) { return hasBackspaceDown ? (long)unchecked(time - backspaceDownTime) : -1L; }
        public long DeltaHangul(uint time) { return hasHangul ? (long)unchecked(time - hangulTime) : -1L; }

        public bool Suppress(int hookCode, uint vk, bool isDown, uint time, uint scan, uint flags)
        {
            if (hookCode < 0) return false;
            if (vk != MediaNext)
            {
                // Only the first down opens these short candidate windows. Autorepeat
                // must not turn a held key into a continuous media-command filter.
                if (vk == Q)
                {
                    if (isDown && !qHeld && !Paused) { hasQDown = true; qDownTime = time; }
                    qHeld = isDown;
                }
                else if (vk == Backspace)
                {
                    if (isDown && !backspaceHeld && !Paused) { hasBackspaceDown = true; backspaceDownTime = time; }
                    backspaceHeld = isDown;
                }
                if (!Paused && !isDown)
                {
                    if (vk == T) { hasT = true; tTime = time; }
                    else if (vk == O) { hasO = true; oTime = time; }
                    else if (vk == D) { hasD = true; dTime = time; }
                    else if (vk == Y) { hasY = true; yTime = time; }
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
            bool afterD = hasD && unchecked(time - dTime) <= WindowMs;
            bool afterY = hasY && unchecked(time - yTime) <= WindowMs;
            bool afterQDown = hasQDown && qHeld && unchecked(time - qDownTime) <= CandidateDownWindowMs;
            bool afterBackspaceDown = hasBackspaceDown && backspaceHeld && unchecked(time - backspaceDownTime) <= CandidateDownWindowMs;
            bool afterHangul = hasHangul && unchecked(time - hangulTime) <= WindowMs;
            if (!afterT && !afterO && !afterD && !afterY && !afterQDown && !afterBackspaceDown && !afterHangul) { passedMediaDown = true; return false; }
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
        public long deltaSinceDKeyUpMs { get; set; }
        public long deltaSinceYKeyUpMs { get; set; }
        public long deltaSinceQKeyDownMs { get; set; }
        public long deltaSinceBackspaceKeyDownMs { get; set; }
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
                    deltaSinceDKeyUpMs = ctx.policy.DeltaD(data.time),
                    deltaSinceYKeyUpMs = ctx.policy.DeltaY(data.time),
                    deltaSinceQKeyDownMs = ctx.policy.DeltaQDown(data.time),
                    deltaSinceBackspaceKeyDownMs = ctx.policy.DeltaBackspaceDown(data.time),
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
                var status = new { schemaVersion = 3, utility = "ImeMediaGuard", version = "1.1.0", policyVersion = SuppressionPolicy.Version, pid = Process.GetCurrentProcess().Id,
                    state = currentState,
                    hookInstalled = hook != IntPtr.Zero, windowMs = SuppressionPolicy.WindowMs,
                    candidateDownWindowMs = SuppressionPolicy.CandidateDownWindowMs,
                    candidateDownEligibility = "initial Q/Backspace down only; until release; autorepeat does not extend window",
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
            test("Q keyup alone does not arm media suppression", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x51, false, 100, 16, 128);
                Check(!p.Suppress(0, 0xB0, true, 105, 0, 17)); Check(!p.Suppress(0, 0xB0, false, 110, 0, 145));
            });
            test("D keyup arms eligible media pair without suppressing D", delegate {
                var p = new SuppressionPolicy(); Check(p.DeltaD(99) == -1);
                Check(!p.Suppress(0, 0x44, false, 100, 32, 128)); Check(p.DeltaD(101) == 1);
                Check(p.Suppress(0, 0xB0, true, 101, 0, 17));
                Check(p.Suppress(0, 0xB0, true, 400, 0, 17)); Check(p.Suppress(0, 0xB0, false, 500, 0, 145));
                Check(!p.Suppress(0, 0xB0, true, 501, 0, 17));
            });
            test("D keydown alone does not arm suppression", delegate {
                var p = new SuppressionPolicy(); Check(!p.Suppress(0, 0x44, true, 100, 32, 0));
                Check(p.DeltaD(101) == -1); Check(!p.Suppress(0, 0xB0, true, 101, 0, 17));
                Check(!p.Suppress(0, 0xB0, false, 102, 0, 145));
            });
            test("D 150ms boundary blocks but 151ms passes", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x44, false, 100, 32, 128);
                Check(p.Suppress(0, 0xB0, true, 250, 0, 17)); Check(p.Suppress(0, 0xB0, false, 250, 0, 145));
                Check(!p.Suppress(0, 0xB0, true, 251, 0, 17)); Check(!p.Suppress(0, 0xB0, false, 252, 0, 145));
            });
            test("Pause and resume clear D trigger including D released during pause", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x44, false, 100, 32, 128); p.SetPaused(true);
                Check(p.DeltaD(105) == -1); p.Suppress(0, 0x44, false, 110, 32, 128);
                Check(p.DeltaD(115) == -1); p.SetPaused(false); Check(p.DeltaD(115) == -1);
                Check(!p.Suppress(0, 0xB0, true, 120, 0, 17)); Check(!p.Suppress(0, 0xB0, false, 121, 0, 145));
                p.Suppress(0, 0x44, false, 130, 32, 128); Check(p.Suppress(0, 0xB0, true, 131, 0, 17));
            });
            test("Negative hook code cannot arm D", delegate {
                var p = new SuppressionPolicy(); Check(!p.Suppress(-1, 0x44, false, 100, 32, 128));
                Check(p.DeltaD(101) == -1); Check(!p.Suppress(0, 0xB0, true, 101, 0, 17));
                Check(!p.Suppress(0, 0xB0, false, 102, 0, 145));
            });
            test("D window stays accurate across DWORD wrap", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x44, false, uint.MaxValue - 4, 32, 128);
                Check(p.DeltaD(2) == 7); Check(p.Suppress(0, 0xB0, true, 2, 0, 17));
                Check(p.Suppress(0, 0xB0, false, 3, 0, 145));
                Check(p.DeltaD(146) == 151); Check(!p.Suppress(0, 0xB0, true, 146, 0, 17));
            });
            test("D trigger leaves non-injected and nonzero-scan events independent", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x44, false, 100, 32, 128);
                Check(!p.Suppress(0, 0xB0, true, 101, 0, 1)); Check(!p.Suppress(0, 0xB0, false, 102, 0, 129));
                Check(!p.Suppress(0, 0xB0, true, 103, 25, 17)); Check(!p.Suppress(0, 0xB0, false, 104, 25, 145));
                Check(p.Suppress(0, 0xB0, true, 105, 0, 17));
                Check(!p.Suppress(0, 0xB0, true, 106, 0, 1)); Check(!p.Suppress(0, 0xB0, false, 107, 0, 129));
                Check(!p.Suppress(0, 0xB0, true, 108, 25, 17)); Check(!p.Suppress(0, 0xB0, false, 109, 25, 145));
                Check(p.Suppress(0, 0xB0, false, 300, 0, 145));
            });
            test("Forwarded media press remains forwarded across D release", delegate {
                var p = new SuppressionPolicy(); Check(!p.Suppress(0, 0xB0, true, 90, 0, 17));
                p.Suppress(0, 0x44, false, 100, 32, 128); Check(!p.Suppress(0, 0xB0, true, 101, 0, 17));
                Check(!p.Suppress(0, 0xB0, false, 102, 0, 145)); Check(p.Suppress(0, 0xB0, true, 103, 0, 17));
                Check(p.Suppress(0, 0xB0, false, 104, 0, 145));
            });
            test("Y release covers observed short intervals and keeps original keys", delegate {
                foreach (uint delay in new uint[] { 1, 2, 5 }) {
                    var p = new SuppressionPolicy(); Check(!p.Suppress(0, SuppressionPolicy.Y, true, 10, 21, 0));
                    Check(!p.Suppress(0, SuppressionPolicy.Y, false, 100, 21, 128));
                    Check(p.DeltaY(100 + delay) == delay);
                    Check(p.Suppress(0, 0xB0, true, 100 + delay, 0, 17)); Check(p.Suppress(0, 0xB0, false, 110, 0, 145));
                }
            });
            test("Y release 150ms boundary blocks and 151ms passes", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, SuppressionPolicy.Y, false, 100, 21, 128);
                Check(p.Suppress(0, 0xB0, true, 250, 0, 17)); Check(p.Suppress(0, 0xB0, false, 250, 0, 145));
                Check(!p.Suppress(0, 0xB0, true, 251, 0, 17));
            });
            test("Y down and negative-hook Y release cannot arm", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, SuppressionPolicy.Y, true, 100, 21, 0);
                p.Suppress(-1, SuppressionPolicy.Y, false, 101, 21, 128);
                Check(p.DeltaY(102) == -1); Check(!p.Suppress(0, 0xB0, true, 102, 0, 17));
            });
            test("Y pause clears release times from before and during pause", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, SuppressionPolicy.Y, false, 100, 21, 128);
                p.SetPaused(true); p.Suppress(0, SuppressionPolicy.Y, false, 101, 21, 128); p.SetPaused(false);
                Check(p.DeltaY(102) == -1); Check(!p.Suppress(0, 0xB0, true, 102, 0, 17));
            });
            test("Y release window survives DWORD wrap", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, SuppressionPolicy.Y, false, uint.MaxValue - 4, 21, 128);
                Check(p.DeltaY(2) == 7); Check(p.Suppress(0, 0xB0, true, 145, 0, 17));
                Check(p.Suppress(0, 0xB0, false, 145, 0, 145)); Check(!p.Suppress(0, 0xB0, true, 146, 0, 17));
            });
            test("Y gate passes real media and injected nonzero-scan media", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, SuppressionPolicy.Y, false, 100, 21, 128);
                Check(!p.Suppress(0, 0xB0, true, 101, 0, 1)); Check(!p.Suppress(0, 0xB0, false, 102, 0, 129));
                Check(!p.Suppress(0, 0xB0, true, 103, 25, 17)); Check(!p.Suppress(0, 0xB0, false, 104, 25, 145));
                Check(p.Suppress(0, 0xB0, true, 105, 0, 17)); p.SetPaused(true);
                Check(p.Suppress(0, 0xB0, true, 400, 0, 17)); Check(p.Suppress(0, 0xB0, false, 500, 0, 145));
            });
            foreach (uint candidate in new uint[] { SuppressionPolicy.Q, SuppressionPolicy.Backspace })
            {
                uint key = candidate;
                string label = key == SuppressionPolicy.Q ? "Q" : "Backspace";
                test(label + " initial down covers 6ms candidate and owns matching media pair", delegate {
                    var p = new SuppressionPolicy(); Check(!p.Suppress(0, key, true, 100, 16, 0));
                    Check(p.Suppress(0, 0xB0, true, 106, 0, 17)); Check(!p.Suppress(0, key, false, 110, 16, 128));
                    Check(p.Suppress(0, 0xB0, true, 400, 0, 17)); Check(p.Suppress(0, 0xB0, false, 500, 0, 145));
                });
                test(label + " 30ms boundary blocks and 31ms passes", delegate {
                    var p = new SuppressionPolicy(); p.Suppress(0, key, true, 100, 16, 0);
                    Check(p.Suppress(0, 0xB0, true, 130, 0, 17)); Check(p.Suppress(0, 0xB0, false, 130, 0, 145));
                    Check(!p.Suppress(0, 0xB0, true, 131, 0, 17));
                });
                test(label + " release ends short gate and does not open a release window", delegate {
                    var p = new SuppressionPolicy(); p.Suppress(0, key, true, 100, 16, 0);
                    p.Suppress(0, key, false, 101, 16, 128); Check(!p.Suppress(0, 0xB0, true, 102, 0, 17));
                    var loneUp = new SuppressionPolicy(); loneUp.Suppress(0, key, false, 100, 16, 128);
                    Check(!loneUp.Suppress(0, 0xB0, true, 101, 0, 17));
                });
                test(label + " held repeats cannot extend gate but a fresh press can", delegate {
                    var p = new SuppressionPolicy(); p.Suppress(0, key, true, 100, 16, 0);
                    p.Suppress(0, key, true, 200, 16, 0); Check(!p.Suppress(0, 0xB0, true, 201, 0, 17));
                    Check(!p.Suppress(0, 0xB0, false, 202, 0, 145)); p.Suppress(0, key, false, 203, 16, 128);
                    p.Suppress(0, key, true, 204, 16, 0); Check(p.Suppress(0, 0xB0, true, 205, 0, 17));
                });
                test(label + " pause and resume do not turn held repeat into new press", delegate {
                    var p = new SuppressionPolicy(); p.Suppress(0, key, true, 100, 16, 0);
                    p.SetPaused(true); p.Suppress(0, key, true, 101, 16, 0); p.SetPaused(false);
                    p.Suppress(0, key, true, 102, 16, 0); Check(!p.Suppress(0, 0xB0, true, 103, 0, 17));
                    Check(!p.Suppress(0, 0xB0, false, 104, 0, 145)); p.Suppress(0, key, false, 105, 16, 128);
                    p.Suppress(0, key, true, 106, 16, 0); Check(p.Suppress(0, 0xB0, true, 107, 0, 17));
                });
                test(label + " first press while paused remains unarmed after resume", delegate {
                    var p = new SuppressionPolicy(); p.SetPaused(true); p.Suppress(0, key, true, 100, 16, 0);
                    p.SetPaused(false); p.Suppress(0, key, true, 101, 16, 0);
                    Check(!p.Suppress(0, 0xB0, true, 102, 0, 17));
                });
                test(label + " negative hook cannot arm or mark a key as held", delegate {
                    var p = new SuppressionPolicy(); Check(!p.Suppress(-1, key, true, 100, 16, 0));
                    Check(!p.Suppress(0, 0xB0, true, 101, 0, 17)); Check(!p.Suppress(0, 0xB0, false, 102, 0, 145));
                    p.Suppress(0, key, true, 103, 16, 0); Check(p.Suppress(0, 0xB0, true, 104, 0, 17));
                });
                test(label + " short gate survives DWORD wrap", delegate {
                    var p = new SuppressionPolicy(); p.Suppress(0, key, true, uint.MaxValue - 4, 16, 0);
                    Check(p.Suppress(0, 0xB0, true, 25, 0, 17)); Check(p.Suppress(0, 0xB0, false, 25, 0, 145));
                    Check(!p.Suppress(0, 0xB0, true, 26, 0, 17));
                });
                test(label + " short gate does not suppress hardware or nonzero-scan media", delegate {
                    var p = new SuppressionPolicy(); p.Suppress(0, key, true, 100, 16, 0);
                    Check(!p.Suppress(0, 0xB0, true, 101, 0, 1)); Check(!p.Suppress(0, 0xB0, false, 102, 0, 129));
                    Check(!p.Suppress(0, 0xB0, true, 103, 25, 17)); Check(!p.Suppress(0, 0xB0, false, 104, 25, 145));
                    Check(p.Suppress(0, 0xB0, true, 105, 0, 17)); p.SetPaused(true);
                    Check(!p.Suppress(0, 0xB0, false, 106, 25, 145)); Check(p.Suppress(0, 0xB0, false, 400, 0, 145));
                });
                test(label + " new trigger cannot steal an already forwarded media press", delegate {
                    var p = new SuppressionPolicy(); Check(!p.Suppress(0, 0xB0, true, 90, 0, 17));
                    p.Suppress(0, key, true, 100, 16, 0); Check(!p.Suppress(0, 0xB0, true, 101, 0, 17));
                    Check(!p.Suppress(0, 0xB0, false, 102, 0, 145)); Check(p.Suppress(0, 0xB0, true, 103, 0, 17));
                });
            }
            test("Q and Backspace held states and clocks are independent", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, SuppressionPolicy.Q, true, 100, 16, 0);
                p.Suppress(0, SuppressionPolicy.Backspace, true, 120, 14, 0);
                p.Suppress(0, SuppressionPolicy.Q, false, 121, 16, 128);
                Check(p.DeltaQDown(149) == 49 && p.DeltaBackspaceDown(149) == 29);
                Check(p.Suppress(0, 0xB0, true, 149, 0, 17)); Check(p.Suppress(0, 0xB0, false, 150, 0, 145));
                p.Suppress(0, SuppressionPolicy.Backspace, false, 151, 14, 128);
                Check(!p.Suppress(0, 0xB0, true, 152, 0, 17));
            });
            test("K alone is not a trigger; T then K overlap uses existing T window", delegate {
                var p = new SuppressionPolicy(); p.Suppress(0, 0x4B, true, 100, 37, 0);
                Check(!p.Suppress(0, 0xB0, true, 106, 0, 17)); Check(!p.Suppress(0, 0xB0, false, 107, 0, 145));
                p.Suppress(0, 0x54, false, 200, 20, 128); p.Suppress(0, 0x4B, true, 284, 37, 0);
                Check(p.Suppress(0, 0xB0, true, 290, 0, 17)); Check(p.Suppress(0, 0xB0, false, 291, 0, 145));
            });
            var report = new { version = "1.1.0", policyVersion = SuppressionPolicy.Version, passed = failures == 0, total = results.Count, failures = failures,
                testedUtc = DateTime.UtcNow.ToString("o"), globalHookInstalled = false, simulatedKeys = false, tests = results };
            File.WriteAllText(Path.GetFullPath(reportPath), new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
            return failures == 0 ? 0 : 1;
        }

        private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
    }
}
