using System;
using System.Collections.Generic;
using System.Threading;

namespace TypingTune
{
    public static class CoreTests
    {
        private static GuardPolicy Enabled()
        {
            GuardProfile value = GuardProfile.Default(); value.Enabled = true;
            return new GuardPolicy(value);
        }

        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); }

        private static bool Media(GuardPolicy policy, bool down, uint time)
        { return policy.Suppress(0, 176, down, time, 0, down ? 17u : 145u); }

        private static void Key(GuardPolicy policy, uint key, bool down, uint time)
        { Check(!policy.Suppress(0, key, down, time, 1, down ? 0u : 128u), "An ordinary key was blocked."); }

        private static void Add(List<Dictionary<string, object>> tests, string name, Action body)
        {
            try { body(); tests.Add(new Dictionary<string, object> { { "name", name }, { "passed", true } }); }
            catch (Exception ex) { tests.Add(new Dictionary<string, object> { { "name", name }, { "passed", false }, { "error", ex.Message } }); }
        }

        public static Dictionary<string, object> Run()
        {
            List<Dictionary<string, object>> tests = new List<Dictionary<string, object>>();
            Add(tests, "default-disabled-and-profile-cloned", delegate {
                GuardProfile value = GuardProfile.Default(); Check(!value.Enabled, "Default guard must be disabled.");
                GuardPolicy policy = new GuardPolicy(value); value.Enabled = true; value.ReleaseKeys[0] = 65;
                Key(policy, 84, false, 100); Check(!Media(policy, true, 101), "Default guard blocked input.");
                Check(policy.Profile.ReleaseKeys[0] == 84, "Caller changed the internal profile.");
            });
            foreach (int releaseKey in GuardProfile.Default().ReleaseKeys)
            {
                int key = releaseKey;
                Add(tests, "release-" + key + "-inclusive-150ms", delegate {
                    GuardPolicy policy = Enabled(); Key(policy, (uint)key, false, 1000);
                    Check(Media(policy, true, 1150), "150ms boundary should match.");
                    Check(Media(policy, false, 1400), "The matching up must remain blocked outside the window.");
                    Check(policy.CompletedBlockedPress, "The completed press was not identified.");
                });
                Add(tests, "release-" + key + "-outside-151ms", delegate {
                    GuardPolicy policy = Enabled(); Key(policy, (uint)key, false, 1000);
                    Check(!Media(policy, true, 1151), "151ms must pass.");
                    Key(policy, (uint)key, false, 1152);
                    Check(!Media(policy, false, 1153), "The passed down owns its up.");
                });
                Add(tests, "release-" + key + "-down-does-not-open-window", delegate {
                    GuardPolicy policy = Enabled(); Key(policy, (uint)key, true, 1000);
                    Check(!Media(policy, true, 1001), "A release candidate's down opened a window.");
                });
            }
            foreach (int pressKey in GuardProfile.Default().PressKeys)
            {
                int key = pressKey;
                Add(tests, "press-" + key + "-inclusive-30ms", delegate {
                    GuardPolicy policy = Enabled(); Key(policy, (uint)key, true, 1000);
                    Check(Media(policy, true, 1030), "30ms boundary should match.");
                    Key(policy, (uint)key, false, 1031); Check(Media(policy, false, 2000), "Blocked up must pair after release.");
                });
                Add(tests, "press-" + key + "-outside-31ms", delegate {
                    GuardPolicy policy = Enabled(); Key(policy, (uint)key, true, 1000);
                    Check(!Media(policy, true, 1031), "31ms should pass.");
                });
                Add(tests, "press-" + key + "-keyup-closes-window", delegate {
                    GuardPolicy policy = Enabled(); Key(policy, (uint)key, true, 1000); Key(policy, (uint)key, false, 1001);
                    Check(!Media(policy, true, 1002), "Released press key remained eligible.");
                });
                Add(tests, "press-" + key + "-autorepeat-does-not-extend", delegate {
                    GuardPolicy policy = Enabled(); Key(policy, (uint)key, true, 1000); Key(policy, (uint)key, true, 2000);
                    Check(!Media(policy, true, 2001), "Repeat extended initial down gate.");
                });
                Add(tests, "press-" + key + "-pause-preserves-held-state", delegate {
                    GuardPolicy policy = Enabled(); Key(policy, (uint)key, true, 1000);
                    policy.SetPaused(true); policy.SetPaused(false); Key(policy, (uint)key, true, 2000);
                    Check(!Media(policy, true, 2001), "Repeat after pause became a new press.");
                    Media(policy, false, 2002); Key(policy, (uint)key, false, 2003); Key(policy, (uint)key, true, 2004);
                    Check(Media(policy, true, 2005), "New physical down after keyup did not match.");
                });
                Add(tests, "press-" + key + "-disabled-preserves-held-state", delegate {
                    GuardProfile initial = GuardProfile.Default(); GuardPolicy policy = new GuardPolicy(initial);
                    Key(policy, (uint)key, true, 1000); initial.Enabled = true; policy.ApplyProfile(initial);
                    Key(policy, (uint)key, true, 1001); Check(!Media(policy, true, 1002), "Enabling turned repeat into a new press.");
                });
            }
            Add(tests, "original-letters-modifiers-and-other-media-always-pass", delegate {
                GuardPolicy policy = Enabled(); Key(policy, 84, false, 1000);
                uint[] keys = { 0x41, 0x4D, 0x43, 0x48, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C, 0xB1, 0xB2, 0xB3 };
                foreach (uint key in keys)
                {
                    Check(!policy.Suppress(0, key, true, 1001, 0, 17), "Non-MediaNext down was blocked.");
                    Check(!policy.Suppress(0, key, false, 1002, 0, 145), "Non-MediaNext up was blocked.");
                }
            });
            Add(tests, "non-injected-media-passes-even-during-blocked-pair", delegate {
                GuardPolicy policy = Enabled(); Key(policy, 84, false, 1000); Check(Media(policy, true, 1001), "Setup failed.");
                Check(!policy.Suppress(0, 176, true, 1002, 0, 1), "Hardware down blocked.");
                Check(!policy.Suppress(0, 176, false, 1003, 0, 129), "Hardware up blocked.");
                Check(Media(policy, false, 1004), "Hardware pair disturbed injected ownership.");
            });
            Add(tests, "nonzero-scan-passes-even-during-blocked-pair", delegate {
                GuardPolicy policy = Enabled(); Key(policy, 84, false, 1000); Media(policy, true, 1001);
                Check(!policy.Suppress(0, 176, true, 1002, 1, 17), "Nonzero scan down blocked.");
                Check(!policy.Suppress(0, 176, false, 1003, 1, 145), "Nonzero scan up blocked.");
                Check(Media(policy, false, 1004), "Other scan disturbed ownership.");
            });
            Add(tests, "negative-hook-code-never-mutates-policy", delegate {
                GuardPolicy policy = Enabled();
                Check(!policy.Suppress(-1, 84, false, 1000, 1, 128), "Negative hook blocked.");
                Check(!Media(policy, true, 1001), "Negative hook created a release timestamp.");
            });
            Add(tests, "unmatched-up-passes", delegate { Check(!Media(Enabled(), false, 100), "Orphan up blocked."); });
            Add(tests, "blocked-repeat-and-up-complete-once", delegate {
                GuardPolicy policy = Enabled(); Key(policy, 84, false, 1000); Check(Media(policy, true, 1001), "Initial down not blocked.");
                Check(!policy.CompletedBlockedPress, "A down completed a pair.");
                Check(Media(policy, true, 2000), "Repeat escaped ownership."); Check(!policy.CompletedBlockedPress, "Repeat counted as completion.");
                Check(Media(policy, false, 2001), "Matching up escaped."); Check(policy.CompletedBlockedPress, "Completion missing.");
                Check(!Media(policy, false, 2002), "Extra up should pass."); Check(!policy.CompletedBlockedPress, "Extra up completed twice.");
            });
            Add(tests, "passed-repeat-and-up-keep-ownership", delegate {
                GuardPolicy policy = Enabled(); Check(!Media(policy, true, 1000), "Initial down should pass.");
                Key(policy, 84, false, 1001); Check(!Media(policy, true, 1002), "Repeat was newly blocked.");
                Check(!Media(policy, false, 1003), "Passed pair's up was blocked.");
                Check(Media(policy, true, 1004), "The next independent eligible press was not blocked.");
            });
            Add(tests, "pause-keeps-blocked-pair-and-clears-candidate-times", delegate {
                GuardPolicy policy = Enabled(); Key(policy, 84, false, 1000); Media(policy, true, 1001);
                policy.SetPaused(true); Check(Media(policy, true, 1002), "Paused repeat escaped.");
                Check(Media(policy, false, 1003), "Paused matching up escaped.");
                policy.SetPaused(false); Check(!Media(policy, true, 1004), "Stale release clock survived pause.");
            });
            Add(tests, "paused-release-does-not-seed-future-window", delegate {
                GuardPolicy policy = Enabled(); policy.SetPaused(true); Key(policy, 84, false, 1000);
                policy.SetPaused(false); Check(!Media(policy, true, 1001), "Paused release became eligible.");
            });
            Add(tests, "disable-keeps-blocked-pair-and-no-new-filtering", delegate {
                GuardPolicy policy = Enabled(); Key(policy, 84, false, 1000); Media(policy, true, 1001);
                GuardProfile value = policy.Profile; value.Enabled = false; policy.ApplyProfile(value);
                Check(Media(policy, false, 1002), "Disabling lost pair ownership.");
                Key(policy, 84, false, 1003); Check(!Media(policy, true, 1004), "Disabled policy still starts suppression.");
            });
            Add(tests, "release-time-dword-wrap", delegate {
                GuardPolicy policy = Enabled(); Key(policy, 84, false, uint.MaxValue - 100);
                Check(Media(policy, true, 49), "150ms across DWORD wrap should match.");
            });
            Add(tests, "release-time-dword-wrap-outside-window", delegate {
                GuardPolicy policy = Enabled(); Key(policy, 84, false, uint.MaxValue - 100);
                Check(!Media(policy, true, 50), "151ms across wrap should pass.");
            });
            Add(tests, "press-time-dword-wrap", delegate {
                GuardPolicy policy = Enabled(); Key(policy, 81, true, uint.MaxValue - 10);
                Check(Media(policy, true, 19), "30ms across wrap should match.");
            });
            Add(tests, "old-or-reordered-clock-does-not-match", delegate {
                GuardPolicy policy = Enabled(); Key(policy, 84, false, 1000);
                Check(!Media(policy, true, 999), "Negative elapsed should not match unsigned window.");
            });
            Add(tests, "custom-release-keys-replace-defaults", delegate {
                GuardProfile value = GuardProfile.Default(); value.Enabled = true; value.ReleaseKeys = new int[] { 0x41, 0x48, 0x43 };
                GuardPolicy policy = new GuardPolicy(value); Key(policy, 84, false, 1000);
                Check(!Media(policy, true, 1001), "Removed T key still triggered."); Media(policy, false, 1002);
                Key(policy, 65, false, 1003); Check(Media(policy, true, 1004), "Custom A key did not trigger.");
            });
            Add(tests, "custom-window-boundary", delegate {
                GuardProfile value = GuardProfile.Default(); value.Enabled = true; value.ReleaseWindowMs = 25;
                GuardPolicy policy = new GuardPolicy(value); Key(policy, 84, false, 1000);
                Check(!Media(policy, true, 1026), "Custom window was ignored."); Media(policy, false, 1027);
                Key(policy, 84, false, 2000); Check(Media(policy, true, 2025), "Custom inclusive boundary failed.");
            });
            Add(tests, "new-press-candidate-waits-for-release", delegate {
                GuardPolicy policy = Enabled(); GuardProfile value = policy.Profile; value.PressKeys = new int[] { 65 };
                policy.ApplyProfile(value); Key(policy, 65, true, 1000);
                Check(!Media(policy, true, 1001), "Unknown held state should not trigger."); Media(policy, false, 1002);
                Key(policy, 65, false, 1003); Key(policy, 65, true, 1004);
                Check(Media(policy, true, 1005), "New candidate should work after a release and fresh down.");
            });
            Add(tests, "profile-apply-clears-old-release-window", delegate {
                GuardPolicy policy = Enabled(); Key(policy, 84, false, 1000); policy.ApplyProfile(policy.Profile);
                Check(!Media(policy, true, 1001), "Applying profile retained stale clock.");
            });
            Add(tests, "profile-apply-preserves-passed-pair", delegate {
                GuardPolicy policy = Enabled(); Media(policy, true, 1000); policy.ApplyProfile(policy.Profile);
                Key(policy, 84, false, 1001); Check(!Media(policy, false, 1002), "Profile apply lost passed ownership.");
            });
            Add(tests, "injected-lower-integrity-flag-still-matches", delegate {
                GuardPolicy policy = Enabled(); Key(policy, 84, false, 1000);
                Check(policy.Suppress(0, 176, true, 1001, 0, 0x13), "Combined injection flags should match.");
            });
            Add(tests, "correlation-contains-only-configured-key-timing", delegate {
                GuardPolicy policy = Enabled(); Key(policy, 84, false, 1000); Key(policy, 65, false, 1001);
                List<Dictionary<string, object>> result = policy.Correlation(1010);
                Check(result.Count == 7, "Unexpected number of configured timing entries.");
                foreach (Dictionary<string, object> row in result)
                {
                    Check((int)row["vkCode"] != 65, "Unconfigured key timing was retained.");
                    if ((int)row["vkCode"] == 84) Check((long)row["deltaMs"] == 10, "Native clock delta incorrect.");
                }
            });
            Add(tests, "null-key-arrays-mean-empty", delegate {
                GuardProfile value = GuardProfile.Default(); value.Enabled = true; value.ReleaseKeys = null; value.PressKeys = null;
                GuardPolicy policy = new GuardPolicy(value); Key(policy, 84, false, 1000);
                Check(!Media(policy, true, 1001), "Empty profile should not match.");
            });
            Add(tests, "invalid-profile-does-not-replace-running-profile", delegate {
                GuardPolicy policy = Enabled(); GuardProfile invalid = policy.Profile; invalid.ReleaseKeys = new int[] { 176 };
                bool threw = false; try { policy.ApplyProfile(invalid); } catch (ArgumentException) { threw = true; }
                Check(threw, "MediaNext candidate was accepted."); Key(policy, 84, false, 1000);
                Check(Media(policy, true, 1001), "Rejected profile changed the working policy.");
            });
            Add(tests, "invalid-key-and-time-ranges-rejected", delegate {
                int failures = 0;
                GuardProfile value = GuardProfile.Default(); value.ReleaseWindowMs = 251;
                try { value.Validate(); } catch (ArgumentException) { failures++; }
                value = GuardProfile.Default(); value.PressWindowMs = 51;
                try { value.Validate(); } catch (ArgumentException) { failures++; }
                value = GuardProfile.Default(); value.ReleaseKeys = new int[] { 0 };
                try { value.Validate(); } catch (ArgumentException) { failures++; }
                value = GuardProfile.Default(); value.PressKeys = new int[] { 65, 65 };
                try { value.Validate(); } catch (ArgumentException) { failures++; }
                Check(failures == 4, "Unsafe range was accepted.");
            });
            Add(tests, "recording-off-records-no-ordinary-events", delegate {
                TelemetryStore store = new TelemetryStore();
                store.Add("test", "keydown", new Dictionary<string, object> { { "vkCode", 65 } });
                Check(store.Total == 0 && store.Snapshot().Count == 0, "Recording disabled but data retained.");
            });
            Add(tests, "telemetry-3000-boundary-and-explicit-drop-count", delegate {
                TelemetryStore store = new TelemetryStore(); store.Recording = true;
                for (int i = 1; i <= 3005; i++) store.Add("test", "keydown", new Dictionary<string, object> { { "vkCode", 65 } });
                List<Dictionary<string, object>> rows = store.Snapshot();
                Check(store.Total == 3005 && store.Dropped == 5 && rows.Count == 3000, "Retention accounting incorrect.");
                Check((long)rows[0]["seq"] == 6 && (long)rows[2999]["seq"] == 3005, "FIFO order incorrect.");
            });
            Add(tests, "telemetry-input-and-output-are-deep-copies", delegate {
                TelemetryStore store = new TelemetryStore(); store.Recording = true;
                Dictionary<string, object> nested = new Dictionary<string, object> { { "value", 1 } };
                Dictionary<string, object> source = new Dictionary<string, object> { { "nested", nested }, { "keys", new int[] { 65 } } };
                store.Add("test", "keydown", source); nested["value"] = 2;
                List<Dictionary<string, object>> rows = store.Snapshot();
                Dictionary<string, object> details = (Dictionary<string, object>)rows[0]["details"];
                Dictionary<string, object> saved = (Dictionary<string, object>)details["nested"];
                Check((int)saved["value"] == 1, "Caller changed retained data."); saved["value"] = 3;
                details = (Dictionary<string, object>)store.Snapshot()[0]["details"];
                Check((int)((Dictionary<string, object>)details["nested"])["value"] == 1, "Snapshot changed store data.");
            });
            Add(tests, "telemetry-clear-resets-session-and-sequence", delegate {
                TelemetryStore store = new TelemetryStore(); store.Recording = true; string oldId = store.SessionId;
                store.Add("test", "keydown", null); store.Clear();
                Check(store.SessionId != oldId && store.Total == 0 && store.Dropped == 0 && store.Snapshot().Count == 0, "Clear did not reset session.");
                store.Add("test", "keyup", null); Check((long)store.Snapshot()[0]["seq"] == 1, "Sequence did not restart.");
            });
            Add(tests, "telemetry-time-and-schema-explicit", delegate {
                TelemetryStore store = new TelemetryStore(); store.Recording = true; store.Add("test", "keydown", null);
                Dictionary<string, object> row = store.Snapshot()[0];
                DateTime time = DateTime.Parse((string)row["utc"], null, System.Globalization.DateTimeStyles.RoundtripKind);
                Check(time.Kind == DateTimeKind.Utc && store.StartedUtc.Kind == DateTimeKind.Utc, "UTC identity missing.");
                Check(row.ContainsKey("details") && !row.ContainsKey("data") && (double)row["elapsedMs"] >= 0, "Event schema mismatch.");
            });
            Add(tests, "concurrent-hook-ui-writers-and-snapshots", delegate {
                TelemetryStore store = new TelemetryStore(); store.Recording = true;
                List<Exception> errors = new List<Exception>(); List<Thread> writers = new List<Thread>();
                for (int writer = 0; writer < 3; writer++)
                {
                    Thread thread = new Thread(delegate() {
                        try { for (int i = 0; i < 400; i++) store.Add("thread-test", "keydown", new Dictionary<string, object> { { "vkCode", 65 } }); }
                        catch (Exception ex) { lock (errors) errors.Add(ex); }
                    }); writers.Add(thread); thread.Start();
                }
                for (int i = 0; i < 15; i++)
                {
                    List<Dictionary<string, object>> snapshot = store.Snapshot(); long previous = 0;
                    foreach (Dictionary<string, object> row in snapshot) { long seq = (long)row["seq"]; Check(seq > previous, "Concurrent snapshot lost sequence ordering."); previous = seq; }
                }
                foreach (Thread thread in writers) Check(thread.Join(3000), "Concurrent writer did not complete.");
                Check(errors.Count == 0 && store.Total == 1200 && store.Dropped == 0 && store.RetainedCount == 1200, "Concurrent retention failed.");
            });
            int failed = 0; foreach (Dictionary<string, object> test in tests) if (!(bool)test["passed"]) failed++;
            return new Dictionary<string, object> {
                { "suite", "TypingTune core" }, { "passed", failed == 0 }, { "total", tests.Count },
                { "failed", failed }, { "results", tests },
                { "scope", "Pure deterministic policy and in-memory telemetry tests; no Windows hook installation, global key injection, real hardware or IME typing is exercised." }
            };
        }
    }
}
