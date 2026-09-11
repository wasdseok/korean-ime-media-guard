using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace TypingTune
{
    // Uses synthetic strings/events only; never installs hooks or sends keyboard input.
    public static class DiagnosticTests
    {
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static Dictionary<string, object> Dict(object value) { return (Dictionary<string, object>)value; }
        private static void Add(List<Dictionary<string, object>> tests, string name, Action body)
        {
            try { body(); tests.Add(new Dictionary<string, object> { { "name", name }, { "passed", true } }); }
            catch (Exception ex) { tests.Add(new Dictionary<string, object> { { "name", name }, { "passed", false }, { "error", ex.Message } }); }
        }
        private static Sample Find(string id)
        { foreach (Sample sample in SampleCatalog.All()) if (sample.Id == id) return sample; throw new InvalidOperationException("Missing sample " + id); }
        private static void WithExportFolder(Action<string> body)
        {
            string directory = Path.Combine(Path.GetTempPath(), "TypingTune-export-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { body(directory); }
            finally
            {
                // This unique test-owned folder contains only synthetic files, never user exports.
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
        private static Dictionary<string, object> Ev(long seq, double time, string source, string type, Dictionary<string, object> details)
        { return new Dictionary<string, object> { { "seq", seq }, { "utc", "2026-01-01T00:00:00.0000000Z" }, { "elapsedMs", time }, { "source", source }, { "type", type }, { "details", details } }; }
        private static List<Dictionary<string, object>> MediaEvents(bool blocked)
        {
            return new List<Dictionary<string, object>> {
                Ev(1, 100, "native-hook", "keyup", new Dictionary<string, object> { { "vkCode", 79 }, { "scanCode", 24 }, { "flags", 128 }, { "isInjected", false }, { "blocked", false } }),
                Ev(2, 101, "native-hook", "keydown", new Dictionary<string, object> { { "vkCode", 176 }, { "scanCode", 0 }, { "flags", 17 }, { "isInjected", true }, { "blocked", blocked } }),
                Ev(3, 103, "win32-ime", "composition-end", new Dictionary<string, object> { { "text", "새" } }),
                Ev(4, 104, "native-raw-input", "keydown", new Dictionary<string, object> { { "vkCode", 79 }, { "scanCode", 24 }, { "deviceId", "keyboard-01" } })
            };
        }

        private static List<Dictionary<string, object>> ImeActivityWithoutNativeKeys()
        {
            return new List<Dictionary<string, object>> {
                Ev(1, 1, "win32-ime", "composition-start", new Dictionary<string, object>()),
                Ev(2, 2, "win32-ime", "composition-update", new Dictionary<string, object> { { "composition", "삶" } }),
                Ev(3, 3, "ui", "text-changed", new Dictionary<string, object> { { "text", "삶" } })
            };
        }

        public static Dictionary<string, object> Run()
        {
            List<Dictionary<string, object>> tests = new List<Dictionary<string, object>>();
            Add(tests, "catalog-ids-unique-and-ten-options", delegate {
                HashSet<string> ids = new HashSet<string>();
                foreach (Sample sample in SampleCatalog.All()) { Check(ids.Add(sample.Id), "Duplicate sample ID"); Check(sample.Title.Length > 0 && sample.Description.Length > 0, "Missing sample metadata"); }
                Check(ids.Count == 10, "Expected nine prepared tests plus free input");
            });
            Add(tests, "catalog-covers-all-modern-hangul-and-two-set-keys", delegate {
                StringBuilder text = new StringBuilder(); foreach (Sample sample in SampleCatalog.All()) text.AppendLine(sample.Text);
                Dictionary<string, object> coverage = SampleCatalog.Coverage(text.ToString());
                foreach (string key in new[] { "all26PhysicalKeys", "all7ShiftedKeys", "all19Initials", "all21Medials", "all27Finals", "all11ComplexFinals" }) Check((bool)coverage[key], "Incomplete collection coverage: " + key);
            });
            Add(tests, "pangram-block-covers-26-physical-and-seven-shifted-keys", delegate {
                Dictionary<string, object> coverage = SampleCatalog.Coverage(Find("pangram-keys").Text);
                Check((bool)coverage["all26PhysicalKeys"], "Pangram block misses physical keys");
                Check((bool)coverage["all7ShiftedKeys"], "Pangram block misses shifted keys");
                Check((bool)coverage["all19Initials"] && (bool)coverage["all21Medials"], "Pangram block misses a modern initial or vowel");
            });
            Add(tests, "initial-sample-all-19", delegate { Check((bool)SampleCatalog.Coverage(Find("initials-19").Text)["all19Initials"], "Missing initial"); });
            Add(tests, "vowel-sample-all-21", delegate { Check((bool)SampleCatalog.Coverage(Find("vowels-21").Text)["all21Medials"], "Missing vowel"); });
            Add(tests, "final-sample-all-27", delegate { Check((bool)SampleCatalog.Coverage(Find("finals-27").Text)["all27Finals"], "Missing final"); });
            Add(tests, "complex-final-list-all-11", delegate { Check((bool)SampleCatalog.Coverage(Find("complex-finals-11").Text)["all11ComplexFinals"], "Missing complex final"); });
            Add(tests, "complex-final-natural-sentences-all-11", delegate { Check((bool)SampleCatalog.Coverage(Find("complex-sentences").Text)["all11ComplexFinals"], "Missing complex final in prose"); });
            Add(tests, "requested-words-ssang-ttok-salm-sang", delegate {
                string text = Find("focus-double").Text; foreach (string word in new[] { "쌍", "똑", "삶", "상" }) Check(text.Contains(word), "Missing requested word " + word);
            });
            Add(tests, "empty-text-coverage-does-not-claim-complete", delegate { Check(!(bool)SampleCatalog.Coverage("")["all26PhysicalKeys"], "Empty text claimed coverage"); });
            Add(tests, "compatibility-jamo-do-not-invent-syllable-roles", delegate {
                Dictionary<string, object> coverage = SampleCatalog.Coverage("ㄱㅏㄳ");
                Check((int)coverage["syllableCount"] == 0 && ((List<string>)coverage["initialJamo"]).Count == 0, "Standalone jamo invented syllable roles");
                Check((int)coverage["physicalKeyCount"] == 3, "Compatibility key mapping wrong");
            });
            Add(tests, "nfd-storage-is-not-jamo-separation", delegate {
                Dictionary<string, object> report = DiagnosticExporter.Analyze("삶", "삶".Normalize(NormalizationForm.FormD), null);
                Check((bool)Dict(report["comparison"])["nfcMatch"], "NFD not normalized for comparison");
                Check((int)Dict(report["jamoInspection"])["actualCount"] == 0, "NFD storage incorrectly called standalone jamo");
            });
            Add(tests, "intentional-double-consonants-not-called-errors", delegate {
                Dictionary<string, object> jamo = Dict(DiagnosticExporter.Analyze("ㄲㄲㅆㅃㅉㄸ", "ㄲㄲㅆㅃㅉㄸ", null)["jamoInspection"]);
                Check((bool)jamo["matchesExpectedStandaloneSequence"] && !(bool)jamo["potentialUnexpectedStandaloneJamo"], "Intentional consonant test flagged");
            });
            Add(tests, "unexpected-standalone-jamo-is-candidate-not-root-cause", delegate {
                Dictionary<string, object> jamo = Dict(DiagnosticExporter.Analyze("발생", "발새ㅇ", null)["jamoInspection"]);
                Check((bool)jamo["potentialUnexpectedStandaloneJamo"] && (int)jamo["actualCount"] == 1, "Standalone candidate not counted");
            });
            Add(tests, "free-input-has-no-target-score", delegate {
                Dictionary<string, object> comparison = Dict(DiagnosticExporter.Analyze("", "자유 입력", null)["comparison"]);
                Check(!(bool)comparison["hasExpectedText"] && !(bool)Dict(comparison["editDistance"])["computed"], "Free input received invented target score");
            });
            Add(tests, "unicode-scalar-distance-and-surrogate-offsets", delegate {
                Dictionary<string, object> report = DiagnosticExporter.Analyze("가😀", "가😃", null);
                Check((int)Dict(Dict(report["comparison"])["editDistance"])["distance"] == 1, "Emoji should cost one scalar substitution");
                List<Dictionary<string, object>> points = (List<Dictionary<string, object>>)Dict(report["actual"])["codePoints"];
                Check(points.Count == 2 && (int)points[1]["indexUtf16"] == 1 && (int)points[1]["lengthUtf16"] == 2, "Unicode offsets wrong");
            });
            Add(tests, "invalid-surrogate-is-reported-without-crashing", delegate {
                Dictionary<string, object> actual = Dict(DiagnosticExporter.Analyze("가", "\uD800", null)["actual"]);
                Check((bool)actual["containsInvalidSurrogate"], "Invalid scalar not marked");
            });
            Add(tests, "bounded-edit-distance-has-explicit-skip", delegate {
                Dictionary<string, object> distance = Dict(Dict(DiagnosticExporter.Analyze("가", new string('가', 5000), null)["comparison"])["editDistance"]);
                Check(!(bool)distance["computed"] && distance.ContainsKey("reason"), "Large distance not bounded");
            });
            Add(tests, "line-endings-are-distinguished-from-exact-match", delegate {
                Dictionary<string, object> comparison = Dict(DiagnosticExporter.Analyze("가\n나", "가\r\n나", null)["comparison"]);
                Check(!(bool)comparison["exactMatch"] && (bool)comparison["nfcAndLineEndingMatch"], "Line ending distinction missing");
            });
            Add(tests, "media-ime-time-candidate-is-evidence-based", delegate {
                Dictionary<string, object> inspection = Dict(DiagnosticExporter.Analyze("생", "새ㅇ", MediaEvents(false))["mediaCompositionInspection"]);
                Check((int)inspection["unblockedInjectedScanZeroWithNearbyCompositionEnd"] == 1, "Expected temporal candidate");
            });
            Add(tests, "blocked-media-is-not-counted-as-delivered-candidate", delegate {
                Dictionary<string, object> report = DiagnosticExporter.Analyze("생", "생", MediaEvents(true));
                Check((int)Dict(report["mediaCompositionInspection"])["unblockedInjectedScanZeroWithNearbyCompositionEnd"] == 0, "Blocked media treated as delivered");
                Check((int)Dict(report["eventSummary"])["blockedHookMediaNextKeyDowns"] == 1, "Missing blocked evidence");
            });
            Add(tests, "media-outside-inspection-window-does-not-correlate", delegate {
                List<Dictionary<string, object>> events = MediaEvents(false); events[2]["elapsedMs"] = 252.0;
                Check((int)Dict(DiagnosticExporter.Analyze("", "", events)["mediaCompositionInspection"])["unblockedInjectedScanZeroWithNearbyCompositionEnd"] == 0, "Out-of-window event correlated");
            });
            Add(tests, "hook-and-raw-key-sources-not-merged", delegate {
                Dictionary<string, object> source = Dict(Dict(DiagnosticExporter.Analyze("", "", MediaEvents(false))["observedKeyCoverage"])["byEventSource"]);
                Check(source.ContainsKey("native-raw-input") && !source.ContainsKey("native-hook"), "Hook keyup was counted as keydown");
            });
            Add(tests, "schema-retention-and-pretty-json-round-trip", delegate {
                TelemetryStore store = new TelemetryStore(); store.Recording = true;
                for (int i = 0; i < 3005; i++) store.Add("synthetic-test", "test", new Dictionary<string, object> { { "value", i } });
                Dictionary<string, object> report = DiagnosticExporter.Build(store, null, Find("focus-double"), "삶", new Dictionary<string, object> { { "synthetic", true }, { "note", "Quotes \"[]{}\" and newline\nremain data." } }, null);
                Dictionary<string, object> session = Dict(report["session"]);
                Check((int)session["retainedEvents"] == 3000 && (long)session["droppedEvents"] == 5 && (long)session["firstRetainedSequence"] == 6, "Retention metadata incorrect");
                Check((string)report["schemaVersion"] == DiagnosticExporter.SchemaVersion && report.ContainsKey("llmAnalysisGuide"), "Self-contained schema missing");
                string path = Path.Combine(Path.GetTempPath(), "TypingTune-synthetic-json-" + Guid.NewGuid().ToString("N") + ".json");
                try {
                    DiagnosticExporter.Save(path, report);
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 100 };
                    Dictionary<string, object> roundTrip = serializer.Deserialize<Dictionary<string, object>>(json);
                    Check((string)roundTrip["schemaVersion"] == DiagnosticExporter.SchemaVersion && json.Contains("\n  \""), "JSON round-trip or indentation failed");
                    Check((string)Dict(roundTrip["environmentAndUserContext"])["note"] == "Quotes \"[]{}\" and newline\nremain data.", "Formatter changed string content");
                } finally { if (File.Exists(path)) File.Delete(path); }
            });
            Add(tests, "current-trial-analysis-excludes-previous-trial-events", delegate {
                TelemetryStore store = new TelemetryStore(); store.Recording = true;
                store.Add("native-hook", "keydown", new Dictionary<string, object> { { "vkCode", 176 }, { "scanCode", 0 }, { "flags", 17 }, { "blocked", false } });
                store.Add("native-hook", "keydown", new Dictionary<string, object> { { "vkCode", 65 }, { "scanCode", 30 }, { "flags", 0 }, { "blocked", false } });
                Dictionary<string, object> report = DiagnosticExporter.Build(store, null, Find("focus-double"), "삶", new Dictionary<string, object> { { "currentTrialFirstSequence", 2L } }, null);
                Dictionary<string, object> trial = Dict(report["currentTrial"]);
                Check((int)Dict(trial["eventSummary"])["hookMediaNextKeyDowns"] == 0, "Previous trial media leaked into current analysis");
                Check((int)Dict(trial["eventScope"])["retainedEventsInTrial"] == 1 && ((List<Dictionary<string, object>>)report["events"]).Count == 2, "Current filtering lost full session evidence");
            });
            Add(tests, "capture-health-missing-native-evidence-even-when-text-matches", delegate {
                Dictionary<string, object> report = DiagnosticExporter.Analyze("삶", "삶", ImeActivityWithoutNativeKeys());
                Dictionary<string, object> health = Dict(report["captureHealth"]);
                Check((bool)health["keyEvidenceMissing"] && (string)health["status"] == "incomplete-key-evidence", "Missing native evidence was not reported.");
                Check((string)health["labelKo"] == "수집 불완전" && (bool)Dict(report["comparison"])["exactMatch"], "Capture health overwrote independent text comparison.");
                Check(!(bool)health["hardwareConditionAssessed"] && (bool)health["registrationIsNotDeliveryProof"], "Capture health overclaimed hardware or registration evidence.");
            });
            Add(tests, "capture-health-hook-only-is-observed-not-hardware-health", delegate {
                List<Dictionary<string, object>> events = ImeActivityWithoutNativeKeys();
                events.Add(Ev(4, 4, "native-hook", "keydown", new Dictionary<string, object> { { "vkCode", 65 }, { "scanCode", 30 }, { "flags", 0 } }));
                Dictionary<string, object> report = DiagnosticExporter.Analyze("삶", "사ㄹ", events);
                Dictionary<string, object> health = Dict(report["captureHealth"]);
                Check(!(bool)health["keyEvidenceMissing"] && (string)health["status"] == "native-key-events-observed", "Hook-only capture incorrectly requires Raw Input.");
                Check((int)health["nativeRawInputKeyDowns"] == 0 && (int)health["nativeHookKeyDowns"] == 1, "Capture source counts were merged.");
                Check(!(bool)Dict(report["comparison"])["exactMatch"] && !(bool)health["hardwareConditionAssessed"], "Observed input incorrectly became text/hardware success.");
            });
            Add(tests, "capture-health-raw-only-evidence-is-observed", delegate {
                List<Dictionary<string, object>> events = ImeActivityWithoutNativeKeys();
                events.Add(Ev(4, 4, "native-raw-input", "keydown", new Dictionary<string, object> { { "vkCode", 65 }, { "scanCode", 30 } }));
                Dictionary<string, object> health = Dict(DiagnosticExporter.Analyze("삶", "삶", events)["captureHealth"]);
                Check(!(bool)health["keyEvidenceMissing"] && (bool)health["nativeKeyEventsObserved"] && (int)health["nativeRawInputKeyDowns"] == 1, "Raw-only evidence was discarded.");
            });
            Add(tests, "capture-health-keyup-and-ui-keydown-do-not-fill-native-gap", delegate {
                List<Dictionary<string, object>> events = ImeActivityWithoutNativeKeys();
                events.Add(Ev(4, 4, "native-hook", "keyup", new Dictionary<string, object> { { "vkCode", 65 } }));
                events.Add(Ev(5, 5, "ui", "keydown", new Dictionary<string, object> { { "vkCode", 65 } }));
                Dictionary<string, object> health = Dict(DiagnosticExporter.Analyze("삶", "삶", events)["captureHealth"]);
                Check((bool)health["keyEvidenceMissing"] && (int)health["nativeHookKeyDowns"] == 0, "Non-native or keyup evidence masked missing native keydowns.");
            });
            Add(tests, "capture-health-empty-and-single-paste-remain-undetermined", delegate {
                Dictionary<string, object> empty = Dict(DiagnosticExporter.Analyze("삶", "", null)["captureHealth"]);
                Check(!(bool)empty["keyEvidenceMissing"] && (string)empty["status"] == "insufficient-activity", "Empty test incorrectly claimed capture failure or success.");
                List<Dictionary<string, object>> events = new List<Dictionary<string, object>> {
                    Ev(1, 1, "win32-ime", "paste-request", new Dictionary<string, object>()),
                    Ev(2, 2, "ui", "text-changed", new Dictionary<string, object> { { "text", "붙여넣기" } })
                };
                Dictionary<string, object> pasted = Dict(DiagnosticExporter.Analyze("", "붙여넣기", events)["captureHealth"]);
                Check(!(bool)pasted["keyEvidenceMissing"] && (string)pasted["status"] == "insufficient-activity" && (int)pasted["pasteRequests"] == 1, "A single paste was treated as sufficient physical typing evidence.");
            });
            Add(tests, "capture-health-ui-text-threshold-without-ime", delegate {
                List<Dictionary<string, object>> events = new List<Dictionary<string, object>> {
                    Ev(1, 1, "ui", "text-changed", new Dictionary<string, object> { { "text", "a" } }),
                    Ev(2, 2, "ui", "text-changed", new Dictionary<string, object> { { "text", "ab" } })
                };
                Check(!(bool)Dict(DiagnosticExporter.Analyze("abc", "ab", events)["captureHealth"])["keyEvidenceMissing"], "Below-threshold activity was flagged.");
                events.Add(Ev(3, 3, "ui", "text-changed", new Dictionary<string, object> { { "text", "abc" } }));
                Check((bool)Dict(DiagnosticExporter.Analyze("abc", "abc", events)["captureHealth"])["keyEvidenceMissing"], "Text activity threshold failed without IME events.");
            });
            Add(tests, "capture-health-current-trial-excludes-prior-native-events", delegate {
                TelemetryStore store = new TelemetryStore(); store.Recording = true;
                store.Add("native-hook", "keydown", new Dictionary<string, object> { { "vkCode", 65 } });
                foreach (Dictionary<string, object> ev in ImeActivityWithoutNativeKeys())
                    store.Add((string)ev["source"], (string)ev["type"], Dict(ev["details"]));
                Dictionary<string, object> report = DiagnosticExporter.Build(store, null, Find("focus-double"), "삶", new Dictionary<string, object> { { "currentTrialFirstSequence", 2L } }, null);
                Dictionary<string, object> top = Dict(report["captureHealth"]), trial = Dict(Dict(report["currentTrial"])["captureHealth"]);
                Check((bool)top["keyEvidenceMissing"] && (bool)trial["keyEvidenceMissing"], "Previous-trial native input hid the current capture gap.");
                Check((int)top["nativeHookKeyDowns"] == 0 && (string)report["schemaVersion"] == "2.0.0", "Capture-health addition changed scope or schema version.");
            });
            Add(tests, "automatic-export-round-trips-complete-diagnostic-before-publication", delegate {
                WithExportFolder(delegate(string directory) {
                    TelemetryStore store = new TelemetryStore(); store.Recording = true;
                    store.Add("ui", "text-changed", new Dictionary<string, object> { { "text", "쌍 똑 삶 상" } });
                    Dictionary<string, object> report = DiagnosticExporter.Build(store, null, Find("focus-double"), "쌍 똑 삶 상", new Dictionary<string, object> { { "synthetic", true } }, null);
                    string path = DiagnosticExporter.SaveNewToFolder(directory, report, "focus-double");
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    Dictionary<string, object> saved = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 100 }.Deserialize<Dictionary<string, object>>(json);
                    Check(Path.IsPathRooted(path) && Path.GetDirectoryName(path) == directory, "Export path is not the selected absolute directory.");
                    Check((string)Dict(Dict(saved["currentTrial"])["actual"])["text"] == "쌍 똑 삶 상", "Final typed text was not preserved.");
                    Check((string)Dict(saved["sample"])["id"] == "focus-double" && Convert.ToInt32(Dict(saved["session"])["retainedEvents"]) == 1, "Sample or event evidence missing.");
                    Check(json.Contains("\n  \"") && Directory.GetFiles(directory).Length == 1 && Directory.GetFiles(directory, "*.tmp").Length == 0, "Export is not pretty JSON or left a temporary file.");
                });
            });
            Add(tests, "automatic-export-rapid-saves-preserve-both-files", delegate {
                WithExportFolder(delegate(string directory) {
                    string first = DiagnosticExporter.SaveNewToFolder(directory, new Dictionary<string, object> { { "attempt", 1 } }, "same-sample");
                    string firstJson = File.ReadAllText(first, Encoding.UTF8);
                    string second = DiagnosticExporter.SaveNewToFolder(directory, new Dictionary<string, object> { { "attempt", 2 } }, "same-sample");
                    Check(first != second && Directory.GetFiles(directory, "*.json").Length == 2, "Rapid export reused a destination.");
                    Check(File.ReadAllText(first, Encoding.UTF8) == firstJson, "Second export overwrote the first report.");
                    Check(Convert.ToInt32(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(second, Encoding.UTF8))["attempt"]) == 2, "Second report is not independently readable.");
                });
            });
            Add(tests, "automatic-export-invalid-destination-preserves-existing-files", delegate {
                WithExportFolder(delegate(string directory) {
                    string original = Path.Combine(directory, "existing.json");
                    File.WriteAllText(original, "original synthetic data", Encoding.UTF8);
                    foreach (string invalid in new[] { Path.Combine(directory, "missing-directory"), original })
                    {
                        bool failed = false;
                        try { DiagnosticExporter.SaveNewToFolder(invalid, new Dictionary<string, object> { { "value", 1 } }, "sample"); }
                        catch (DirectoryNotFoundException) { failed = true; }
                        Check(failed, "Invalid export directory was accepted or silently redirected.");
                    }
                    Check(File.ReadAllText(original, Encoding.UTF8) == "original synthetic data" && Directory.GetFiles(directory).Length == 1, "Failed export changed an existing file.");
                    Check(!Directory.Exists(Path.Combine(directory, "missing-directory")), "Failed export created an unexpected destination.");
                });
            });
            Add(tests, "automatic-export-sanitizes-untrusted-sample-path-components", delegate {
                WithExportFolder(delegate(string directory) {
                    string path = DiagnosticExporter.SaveNewToFolder(directory, new Dictionary<string, object> { { "value", "sample metadata remains data" } }, "..\\..\\C:/outside\\CON:<bad>|?*\n" + new string('x', 100));
                    string name = Path.GetFileName(path);
                    Check(Path.GetDirectoryName(Path.GetFullPath(path)) == Path.GetFullPath(directory), "Sample ID escaped the export directory.");
                    Check(name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.Length < 140 && name.EndsWith(".json", StringComparison.Ordinal), "Unsafe or unbounded sample ID reached the filename.");
                    Check(Directory.GetDirectories(directory).Length == 0 && Directory.GetFiles(directory).Length == 1, "Sample ID created nested output.");
                });
            });
            Add(tests, "automatic-export-serialization-failure-leaves-no-partial-file", delegate {
                WithExportFolder(delegate(string directory) {
                    string existing = Path.Combine(directory, "other.json");
                    File.WriteAllText(existing, "untouched", Encoding.UTF8);
                    Dictionary<string, object> circular = new Dictionary<string, object>(); circular["self"] = circular;
                    bool failed = false;
                    try { DiagnosticExporter.SaveNewToFolder(directory, circular, "broken-report"); }
                    catch (InvalidOperationException) { failed = true; }
                    catch (ArgumentException) { failed = true; }
                    Check(failed, "Circular diagnostic data was accepted.");
                    Check(Directory.GetFiles(directory).Length == 1 && Directory.GetFiles(directory, "*.tmp").Length == 0, "Serialization failure left a partial export.");
                    Check(File.ReadAllText(existing, Encoding.UTF8) == "untouched", "Serialization failure touched another file.");
                });
            });
            int passed = 0; foreach (Dictionary<string, object> test in tests) if ((bool)test["passed"]) passed++;
            return new Dictionary<string, object> { { "suite", "TypingTune samples and diagnostic export" }, { "passed", passed }, { "failed", tests.Count - passed }, { "total", tests.Count }, { "allPassed", passed == tests.Count }, { "tests", tests }, { "scope", "Synthetic unit tests only. No physical key actuation, global keyboard hook, OS driver change, or real typing verification." } };
        }
    }
}
