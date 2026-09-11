using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TypingTune
{
    public sealed class GuardProfile
    {
        public string Name;
        public bool Enabled;
        public int[] ReleaseKeys;
        public int[] PressKeys;
        public int ReleaseWindowMs;
        public int PressWindowMs;

        public static GuardProfile Default()
        {
            return new GuardProfile {
                Name = "MediaNext timing guard", Enabled = false,
                ReleaseKeys = new int[] { 0x54, 0x4F, 0x44, 0x59, 0x15 },
                PressKeys = new int[] { 0x51, 0x08 },
                ReleaseWindowMs = 150, PressWindowMs = 30
            };
        }

        public GuardProfile Copy()
        {
            return new GuardProfile { Name = Name, Enabled = Enabled,
                ReleaseKeys = ReleaseKeys == null ? new int[0] : (int[])ReleaseKeys.Clone(),
                PressKeys = PressKeys == null ? new int[0] : (int[])PressKeys.Clone(),
                ReleaseWindowMs = ReleaseWindowMs, PressWindowMs = PressWindowMs };
        }

        public void Validate()
        {
            if (ReleaseWindowMs < 1 || ReleaseWindowMs > 250)
                throw new ArgumentException("키 뗌 시간 범위는 1~250ms여야 합니다.");
            if (PressWindowMs < 1 || PressWindowMs > 50)
                throw new ArgumentException("첫 키 누름 시간 범위는 1~50ms여야 합니다.");
            ValidateKeys(ReleaseKeys); ValidateKeys(PressKeys);
        }

        private static void ValidateKeys(int[] keys)
        {
            if (keys == null) return;
            if (keys.Length > 254) throw new ArgumentException("후보 키가 너무 많습니다.");
            HashSet<int> seen = new HashSet<int>();
            foreach (int key in keys)
            {
                if (key < 1 || key > 254 || key == 0xB0)
                    throw new ArgumentException("후보 키에는 1~254의 가상 키 코드만 지정할 수 있고 MediaNext는 제외됩니다.");
                if (!seen.Add(key)) throw new ArgumentException("후보 키 목록에 중복이 있습니다.");
            }
        }

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object> {
                { "name", Name ?? "Custom" }, { "enabled", Enabled },
                { "releaseKeys", ReleaseKeys ?? new int[0] },
                { "pressKeys", PressKeys ?? new int[0] },
                { "releaseWindowMs", ReleaseWindowMs }, { "pressWindowMs", PressWindowMs },
                { "mediaEligibility", "vkCode=176 (0xB0), scanCode=0, flags & 0x10 != 0" },
                { "pressEligibility", "initial keydown only while held; autorepeat never extends the window" }
            };
        }
    }

    // In-memory ring only. File writing and JSON serialization belong to the UI/export layer.
    // Events are {seq, utc, elapsedMs, source, type, details}; seq starts at 1 after Clear.
    public sealed class TelemetryStore
    {
        public const int Limit = 3000;
        private readonly object gate = new object();
        private readonly Queue<Dictionary<string, object>> events = new Queue<Dictionary<string, object>>(Limit);
        private readonly Stopwatch clock = new Stopwatch();
        private volatile bool recording;
        private long total, dropped;
        public bool Recording { get { return recording; } set { recording = value; } }
        public DateTime StartedUtc { get; private set; }
        public string SessionId { get; private set; }
        public long Total { get { return Interlocked.Read(ref total); } }
        public long Dropped { get { return Interlocked.Read(ref dropped); } }
        public int RetainedCount { get { lock (gate) return events.Count; } }
        public double ElapsedMs { get { return clock.Elapsed.TotalMilliseconds; } }

        public TelemetryStore() { Clear(); }

        public void Clear()
        {
            lock (gate)
            {
                events.Clear(); Interlocked.Exchange(ref total, 0); Interlocked.Exchange(ref dropped, 0);
                StartedUtc = DateTime.UtcNow;
                SessionId = Guid.NewGuid().ToString("D");
                clock.Restart();
            }
        }

        public void Add(string source, string type, Dictionary<string, object> data)
        {
            if (!Recording) return;
            string sessionAtEntry = SessionId;
            object copiedDetails = CopyValue(data ?? new Dictionary<string, object>());
            lock (gate)
            {
                if (!Recording || sessionAtEntry != SessionId) return;
                long sequence = Interlocked.Increment(ref total);
                Dictionary<string, object> row = new Dictionary<string, object> {
                    { "seq", sequence }, { "utc", DateTime.UtcNow.ToString("o") },
                    { "elapsedMs", Math.Round(clock.Elapsed.TotalMilliseconds, 3) },
                    { "source", source ?? "unknown" }, { "type", type ?? "unknown" },
                    { "details", copiedDetails }
                };
                if (events.Count == Limit) { events.Dequeue(); Interlocked.Increment(ref dropped); }
                events.Enqueue(row);
            }
        }

        public List<Dictionary<string, object>> Snapshot()
        {
            // Stored rows are immutable. Copy references under the lock, then perform
            // potentially large export copies without blocking the hook thread.
            Dictionary<string, object>[] rows;
            lock (gate) rows = events.ToArray();
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>(rows.Length);
            foreach (Dictionary<string, object> row in rows)
                result.Add((Dictionary<string, object>)CopyValue(row));
            return result;
        }

        internal static object CopyValue(object value)
        {
            if (value == null || value is string || value.GetType().IsValueType) return value;
            IDictionary<string, object> dictionary = value as IDictionary<string, object>;
            if (dictionary != null)
            {
                Dictionary<string, object> copy = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> pair in dictionary) copy[pair.Key] = CopyValue(pair.Value);
                return copy;
            }
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null)
            {
                List<object> copy = new List<object>();
                foreach (object element in sequence) copy.Add(CopyValue(element));
                return copy;
            }
            throw new ArgumentException("Telemetry supports JSON-compatible primitive values, dictionaries and lists only.");
        }
    }

    // Pure deterministic state machine; no hooks or injected input in policy tests.
    public sealed class GuardPolicy
    {
        public const uint MediaNext = 0xB0;
        private GuardProfile profile;
        private readonly bool[] releaseCandidate = new bool[256], pressCandidate = new bool[256];
        private readonly bool[] hasRelease = new bool[256], hasPress = new bool[256], held = new bool[256];
        private readonly uint[] releaseTime = new uint[256], pressTime = new uint[256];
        private bool blockedPair, passedPair;
        public bool Paused { get; private set; }
        public string LastDecision { get; private set; }
        public bool CompletedBlockedPress { get; private set; }
        public GuardProfile Profile { get { return profile.Copy(); } }

        public GuardPolicy(GuardProfile initial)
        {
            profile = (initial ?? GuardProfile.Default()).Copy(); profile.Validate();
            MapKeys(profile);
        }

        public void ApplyProfile(GuardProfile value)
        {
            if (value == null) throw new ArgumentNullException("value");
            GuardProfile next = value.Copy(); next.Validate();
            bool[] previousPress = (bool[])pressCandidate.Clone();
            profile = next; MapKeys(profile); ClearTimes();
            for (int key = 0; key < held.Length; key++)
            {
                // A newly selected key may already be held. Wait for release before
                // using its first-down gate, rather than interpreting repeat as new down.
                if (pressCandidate[key] && !previousPress[key]) held[key] = true;
                if (!pressCandidate[key]) held[key] = false;
            }
            // An in-flight media press keeps its original pass/block ownership.
        }

        private void MapKeys(GuardProfile value)
        {
            Array.Clear(releaseCandidate, 0, releaseCandidate.Length);
            Array.Clear(pressCandidate, 0, pressCandidate.Length);
            foreach (int key in value.ReleaseKeys) releaseCandidate[key] = true;
            foreach (int key in value.PressKeys) pressCandidate[key] = true;
        }

        private void ClearTimes()
        {
            Array.Clear(hasRelease, 0, hasRelease.Length);
            Array.Clear(hasPress, 0, hasPress.Length);
        }

        public void SetPaused(bool value) { Paused = value; ClearTimes(); }

        public bool Suppress(int hookCode, uint vk, bool down, uint nativeTime, uint scan, uint flags)
        {
            CompletedBlockedPress = false;
            LastDecision = "notMedia";
            if (hookCode < 0) { LastDecision = "negativeHookCode"; return false; }
            if (vk != MediaNext)
            {
                if (vk < 256)
                {
                    int key = (int)vk;
                    if (pressCandidate[key])
                    {
                        if (down && !held[key] && profile.Enabled && !Paused)
                        { hasPress[key] = true; pressTime[key] = nativeTime; }
                        held[key] = down;
                    }
                    if (!down && releaseCandidate[key] && profile.Enabled && !Paused)
                    { hasRelease[key] = true; releaseTime[key] = nativeTime; }
                }
                return false;
            }
            if (scan != 0 || (flags & 0x10) == 0) { LastDecision = "notEligible"; return false; }
            if (blockedPair)
            {
                LastDecision = "blockedPairContinuation";
                if (!down) { blockedPair = false; CompletedBlockedPress = true; }
                return true;
            }
            if (passedPair)
            {
                LastDecision = "passedPairContinuation";
                if (!down) passedPair = false;
                return false;
            }
            if (!down) { LastDecision = "unmatchedKeyUp"; return false; }
            if (Paused || !profile.Enabled)
            {
                passedPair = true; LastDecision = Paused ? "paused" : "disabled"; return false;
            }
            foreach (int key in profile.ReleaseKeys)
            {
                if (hasRelease[key] && unchecked(nativeTime - releaseTime[key]) <= (uint)profile.ReleaseWindowMs)
                { blockedPair = true; LastDecision = "matchedCandidateWindow"; return true; }
            }
            foreach (int key in profile.PressKeys)
            {
                if (hasPress[key] && held[key] && unchecked(nativeTime - pressTime[key]) <= (uint)profile.PressWindowMs)
                { blockedPair = true; LastDecision = "matchedCandidateWindow"; return true; }
            }
            passedPair = true; LastDecision = "outOfWindow"; return false;
        }

        public List<Dictionary<string, object>> Correlation(uint nativeTime)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (int key in profile.ReleaseKeys)
            {
                long delta = hasRelease[key] ? (long)unchecked(nativeTime - releaseTime[key]) : -1L;
                result.Add(new Dictionary<string, object> { { "vkCode", key }, { "trigger", "keyup" },
                    { "deltaMs", delta }, { "windowMs", profile.ReleaseWindowMs },
                    { "withinWindow", delta >= 0 && delta <= profile.ReleaseWindowMs } });
            }
            foreach (int key in profile.PressKeys)
            {
                long delta = hasPress[key] ? (long)unchecked(nativeTime - pressTime[key]) : -1L;
                result.Add(new Dictionary<string, object> { { "vkCode", key }, { "trigger", "initial-keydown" },
                    { "deltaMs", delta }, { "windowMs", profile.PressWindowMs }, { "held", held[key] },
                    { "withinWindow", delta >= 0 && delta <= profile.PressWindowMs && held[key] } });
            }
            return result;
        }
    }

    public sealed class NativeKeyboardMonitor : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const uint RidInput = 0x10000003;
        private readonly TelemetryStore store;
        private readonly Func<bool> shouldRecord;
        private readonly GuardPolicy policy = new GuardPolicy(GuardProfile.Default());
        private readonly HookProc callback;
        private readonly object stateGate = new object();
        private readonly Thread hookThread;
        private readonly ManualResetEvent hookReady = new ManualResetEvent(false);
        private readonly Queue<Dictionary<string, object>> media = new Queue<Dictionary<string, object>>(256);
        private readonly Dictionary<IntPtr, string> devices = new Dictionary<IntPtr, string>();
        private readonly HashSet<string> diagnosticKeySlots = new HashSet<string>();
        private static readonly List<HookProc> failedUnhookRoots = new List<HookProc>();
        private IntPtr hook, rawTarget;
        private volatile bool disposed;
        private bool leftShift, rightShift, genericShift;
        private uint nativeThreadId;
        private long mediaTotal, totalHookEvents, recordedKeyDowns, blockedEvents, blockedPresses;
        private volatile string hookError, rawError, lastHookEventUtc;
        public GuardProfile Profile { get { lock (stateGate) return policy.Profile; } }
        public bool Paused { get { lock (stateGate) return policy.Paused; } }
        public bool HookInstalled { get { lock (stateGate) return !disposed && hook != IntPtr.Zero; } }
        public string Error { get { return hookError == null ? rawError : rawError == null ? hookError : hookError + "; " + rawError; } }
        public long BlockedEvents { get { return Interlocked.Read(ref blockedEvents); } }
        public long BlockedPresses { get { return Interlocked.Read(ref blockedPresses); } }
        public long TotalHookEvents { get { return Interlocked.Read(ref totalHookEvents); } }
        public long RecordedKeyDowns { get { return Interlocked.Read(ref recordedKeyDowns); } }

        public NativeKeyboardMonitor(TelemetryStore store, Func<bool> shouldRecord)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (shouldRecord == null) throw new ArgumentNullException("shouldRecord");
            this.store = store; this.shouldRecord = shouldRecord;
            callback = KeyboardCallback;
            // WH_KEYBOARD_LL callbacks run on their installing thread. Keeping the
            // message pump independent from UI layout, exports and dialogs avoids
            // LowLevelHooksTimeout removal when the UI thread is busy.
            hookThread = new Thread(HookThreadMain) { IsBackground = true, Name = "TypingTune keyboard hook" };
            hookThread.SetApartmentState(ApartmentState.STA);
            hookThread.Start();
            if (!hookReady.WaitOne(3000)) hookError = "Keyboard hook thread initialization timed out.";
        }

        public void ApplyProfile(GuardProfile value) { lock (stateGate) policy.ApplyProfile(value); }
        public void SetPaused(bool value) { lock (stateGate) policy.SetPaused(value); }

        public List<string> DiagnosticKeySlotsSnapshot()
        {
            lock (stateGate) { List<string> result = new List<string>(diagnosticKeySlots); result.Sort(StringComparer.Ordinal); return result; }
        }
        public void ClearDiagnosticKeySlots() { lock (stateGate) diagnosticKeySlots.Clear(); }

        private void HookThreadMain()
        {
            try
            {
                nativeThreadId = GetCurrentThreadId();
                NativeMessage message;
                PeekMessage(out message, IntPtr.Zero, 0, 0, 0); // Create the thread message queue before publishing readiness.
                lock (stateGate)
                {
                    leftShift = (GetAsyncKeyState(0xA0) & 0x8000) != 0;
                    rightShift = (GetAsyncKeyState(0xA1) & 0x8000) != 0;
                    hook = SetWindowsHookEx(WhKeyboardLl, callback, GetModuleHandle(null), 0);
                    if (hook == IntPtr.Zero) hookError = "SetWindowsHookEx failed: " + Marshal.GetLastWin32Error();
                }
                hookReady.Set();
                if (!HookInstalled) return;
                int result;
                while (!disposed && (result = GetMessage(out message, IntPtr.Zero, 0, 0)) != 0)
                {
                    if (result == -1) { hookError = "Keyboard hook message loop failed: " + Marshal.GetLastWin32Error(); break; }
                    TranslateMessage(ref message); DispatchMessage(ref message);
                }
            }
            catch (Exception ex) { hookError = "Keyboard hook thread: " + ex.GetType().Name; }
            finally
            {
                lock (stateGate)
                {
                    if (hook != IntPtr.Zero)
                    {
                        if (!UnhookWindowsHookEx(hook))
                        {
                            hookError = "UnhookWindowsHookEx failed: " + Marshal.GetLastWin32Error();
                            lock (failedUnhookRoots) failedUnhookRoots.Add(callback);
                        }
                        hook = IntPtr.Zero;
                    }
                }
                hookReady.Set();
            }
        }

        private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0 || disposed) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            int message = unchecked((int)wParam.ToInt64());
            bool down = message == 0x0100 || message == 0x0104;
            bool up = message == 0x0101 || message == 0x0105;
            if (!down && !up) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            bool blocked = false;
            try
            {
                KeyboardData native = (KeyboardData)Marshal.PtrToStructure(lParam, typeof(KeyboardData));
                Interlocked.Increment(ref totalHookEvents); lastHookEventUtc = DateTime.UtcNow.ToString("o");
                // This delegate MUST read thread-safe flags only, never WinForms controls.
                bool capture = store.Recording && shouldRecord();
                bool isMedia = native.vkCode == GuardPolicy.MediaNext;
                Dictionary<string, object> details = null;
                lock (stateGate)
                {
                    blocked = policy.Suppress(code, native.vkCode, down, native.time, native.scanCode, native.flags);
                    if (blocked) Interlocked.Increment(ref blockedEvents);
                    if (policy.CompletedBlockedPress) Interlocked.Increment(ref blockedPresses);
                    if (native.vkCode == 0xA0) leftShift = down;
                    else if (native.vkCode == 0xA1) rightShift = down;
                    else if (native.vkCode == 0x10) genericShift = down;
                    bool shift = leftShift || rightShift || genericShift;
                    if (capture && down && native.vkCode >= 65 && native.vkCode <= 90)
                    {
                        string letter = ((char)native.vkCode).ToString(); diagnosticKeySlots.Add(letter);
                        if (shift && "REQTWOP".IndexOf((char)native.vkCode) >= 0) diagnosticKeySlots.Add("Shift+" + letter);
                    }
                    if (capture || isMedia)
                    {
                        details = new Dictionary<string, object> {
                            { "vkCode", native.vkCode }, { "scanCode", native.scanCode }, { "flags", native.flags },
                            { "nativeTimeMs", native.time }, { "isInjected", (native.flags & 0x10) != 0 },
                            { "isLowerIntegrityInjected", (native.flags & 0x02) != 0 },
                            { "isExtended", (native.flags & 0x01) != 0 }, { "altDown", (native.flags & 0x20) != 0 },
                            { "shiftDown", shift }, { "blocked", blocked }
                        };
                        if (isMedia)
                        {
                            details["decision"] = policy.LastDecision;
                            details["correlation"] = policy.Correlation(native.time);
                            details["profileEnabled"] = policy.Profile.Enabled;
                            details["paused"] = policy.Paused;
                            mediaTotal++;
                            if (media.Count == 256) media.Dequeue();
                            media.Enqueue(new Dictionary<string, object> {
                                { "mediaSeq", mediaTotal }, { "utc", lastHookEventUtc },
                                { "type", down ? "keydown" : "keyup" }, { "details", details }
                            });
                        }
                    }
                }
                if (capture && details != null)
                {
                    if (down) Interlocked.Increment(ref recordedKeyDowns);
                    store.Add("native-hook", down ? "keydown" : "keyup", details);
                }
            }
            catch (Exception ex) { hookError = "Keyboard callback: " + ex.GetType().Name; }
            return blocked ? new IntPtr(1) : CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        public void RegisterRawInput(IntPtr formHandle)
        {
            if (disposed) throw new ObjectDisposedException("NativeKeyboardMonitor");
            if (formHandle == IntPtr.Zero) throw new ArgumentException("A window handle is required.");
            RawInputDevice[] registration = new RawInputDevice[] {
                new RawInputDevice { usagePage = 1, usage = 6, flags = 0, target = formHandle }
            };
            if (!RegisterRawInputDevices(registration, 1, (uint)Marshal.SizeOf(typeof(RawInputDevice))))
                rawError = "RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error();
            else { rawTarget = formHandle; rawError = null; }
        }

        // Called by the form's WndProc for WM_INPUT. flags=0 registration is foreground
        // only, and this second focus/recording check restricts capture to the test box.
        public void HandleRawInput(IntPtr lParam)
        {
            if (disposed || !store.Recording || !shouldRecord()) return;
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf(typeof(RawInputHeader));
            if (GetRawInputData(lParam, RidInput, IntPtr.Zero, ref size, headerSize) == uint.MaxValue) return;
            if (size < headerSize || size > 4096) return;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint received = GetRawInputData(lParam, RidInput, buffer, ref size, headerSize);
                if (received == uint.MaxValue || received < headerSize) return;
                RawInputHeader header = (RawInputHeader)Marshal.PtrToStructure(buffer, typeof(RawInputHeader));
                if (header.type != 1 || received < headerSize + Marshal.SizeOf(typeof(RawKeyboard))) return;
                RawKeyboard keyboard = (RawKeyboard)Marshal.PtrToStructure(IntPtr.Add(buffer, (int)headerSize), typeof(RawKeyboard));
                string deviceId;
                if (header.device == IntPtr.Zero) deviceId = "unidentified";
                else if (!devices.TryGetValue(header.device, out deviceId))
                {
                    // Only opaque handles stay in process memory; no paths, serials,
                    // hardware IDs or machine/user names are queried or exported.
                    deviceId = "keyboard-" + (devices.Count + 1).ToString("D2");
                    devices[header.device] = deviceId;
                }
                store.Add("native-raw-input", (keyboard.flags & 1) != 0 ? "keyup" : "keydown",
                    new Dictionary<string, object> {
                        { "vkCode", (uint)keyboard.vkey }, { "scanCode", (uint)keyboard.makeCode },
                        { "rawFlags", (uint)keyboard.flags }, { "message", keyboard.message },
                        { "nativeTimeMs", unchecked((uint)GetMessageTime()) },
                        { "deviceId", deviceId }, { "deviceIdentityScope", "current monitor instance; not stable across restarts" },
                        { "isExtended", (keyboard.flags & 6) != 0 },
                        { "rawBreak", (keyboard.flags & 1) != 0 }
                    });
            }
            catch (Exception ex) { rawError = "Raw input: " + ex.GetType().Name; }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public Dictionary<string, object> SnapshotSummary()
        {
            Dictionary<string, object>[] mediaRows;
            GuardProfile currentProfile;
            bool currentPaused, currentInstalled;
            long currentMediaTotal, currentBlockedEvents, currentBlockedPresses;
            lock (stateGate)
            {
                mediaRows = media.ToArray(); currentProfile = policy.Profile; currentPaused = policy.Paused;
                currentInstalled = !disposed && hook != IntPtr.Zero;
                currentMediaTotal = mediaTotal; currentBlockedEvents = BlockedEvents; currentBlockedPresses = BlockedPresses;
            }
            List<Dictionary<string, object>> recent = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> item in mediaRows)
                recent.Add((Dictionary<string, object>)TelemetryStore.CopyValue(item));
            return new Dictionary<string, object> {
                { "profile", currentProfile.ToDictionary() }, { "paused", currentPaused },
                { "hookInstalled", currentInstalled }, { "rawInputRegistered", rawTarget != IntPtr.Zero && !disposed },
                { "hookThreadId", nativeThreadId }, { "hookThreadAlive", hookThread.IsAlive },
                { "lastHookEventUtc", lastHookEventUtc }, { "totalHookEvents", Interlocked.Read(ref totalHookEvents) },
                { "recordedKeyDowns", RecordedKeyDowns },
                { "rawInputPolicy", rawTarget == IntPtr.Zero ? "Not registered. TypingTune 2.0.1 uses low-level hook plus IME events by default: simultaneous foreground Raw Input registration prevented hook delivery in a local A/B test. The selected keyboard label is user-provided; no per-device attribution is available in this mode." : "Explicit Raw Input registration; not used by the standard TypingTune diagnostic window." },
                { "hookExecution", "Dedicated background STA thread with a native message loop; UI work does not own the hook callback." },
                { "error", Error }, { "blockedEvents", currentBlockedEvents }, { "blockedPresses", currentBlockedPresses },
                { "blockedPressMeaning", "completed blocked keydown/keyup pairs; repeat downs add events only" },
                { "mediaEventsTotal", currentMediaTotal }, { "mediaEventsDropped", Math.Max(0L, currentMediaTotal - recent.Count) },
                { "recentMediaEvents", recent }, { "mediaEventLimit", 256 },
                { "counterScope", "since this monitor instance started, independent of diagnostic recording/reset" },
                { "recordingPolicy", "Ordinary native hook/raw key events enter the 3000-event diagnostic ring only while recording and the diagnostic input is focused. Only MediaNext events and configured candidate timing deltas enter the separate 256-event global media ring." },
                { "deviceIdentityPolicy", "Anonymous keyboard aliases are local to this monitor instance. Device paths, serials, raw handles, user names and computer names are never exported. Raw input may distinguish input handles but cannot prove physical hardware origin or internal/external identity." },
                { "nativeClock", "nativeTimeMs is a DWORD system message tick; compare with unchecked uint subtraction. It is not a UTC timestamp." },
                { "evidenceLimits", "LLKHF_INJECTED identifies a hook flag, not the source process or root cause. A registered hook is not proof that every system event was observed. No keys are synthesized or text repaired by this guard." }
            };
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (rawTarget != IntPtr.Zero)
            {
                RawInputDevice[] remove = new RawInputDevice[] {
                    new RawInputDevice { usagePage = 1, usage = 6, flags = 1, target = IntPtr.Zero }
                };
                if (!RegisterRawInputDevices(remove, 1, (uint)Marshal.SizeOf(typeof(RawInputDevice))))
                    rawError = "Raw input unregister failed: " + Marshal.GetLastWin32Error();
                rawTarget = IntPtr.Zero;
            }
            if (hookThread.IsAlive && nativeThreadId != 0)
            {
                PostThreadMessage(nativeThreadId, 0x0012, UIntPtr.Zero, IntPtr.Zero);
                if (Thread.CurrentThread != hookThread && !hookThread.Join(2000))
                    hookError = "Keyboard hook thread did not exit within 2000ms.";
            }
            if (!hookThread.IsAlive) hookReady.Dispose();
            devices.Clear();
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardData
        { public uint vkCode, scanCode, flags, time; public UIntPtr extraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct RawInputDevice
        { public ushort usagePage, usage; public uint flags; public IntPtr target; }
        [StructLayout(LayoutKind.Sequential)] private struct RawInputHeader
        { public uint type, size; public IntPtr device, wParam; }
        [StructLayout(LayoutKind.Sequential)] private struct RawKeyboard
        { public ushort makeCode, flags, reserved, vkey; public uint message, extraInformation; }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeMessage
        { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public NativePoint point; public uint privateValue; }
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll")] private static extern int GetMessageTime();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out NativeMessage message, IntPtr window, uint first, uint last);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint first, uint last, uint remove);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref NativeMessage message);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref NativeMessage message);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
    }
}
