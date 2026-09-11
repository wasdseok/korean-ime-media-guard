using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace TypingTune
{
    public sealed class MainForm : Form
    {
        private readonly Color Ink = Color.FromArgb(225, 232, 242), Muted = Color.FromArgb(154, 171, 191), PanelColor = Color.FromArgb(24, 34, 49), Accent = Color.FromArgb(71, 213, 173);
        private readonly TelemetryStore store = new TelemetryStore();
        private NativeKeyboardMonitor monitor;
        private readonly UserSettings settings;
        private readonly List<Sample> samples = SampleCatalog.All();
        private readonly List<Dictionary<string, object>> trials = new List<Dictionary<string, object>>();
        private readonly HashSet<string> observedKeys = new HashSet<string>();
        private readonly Dictionary<string, Label> keyLabels = new Dictionary<string, Label>();
        private ComboBox sampleChoice, keyboardChoice;
        private TabControl tabs;
        private Label expected;
        private TableLayoutPanel diagnosisLayout;
        private Panel diagnosisViewport;
        private HoverWheelFilter wheelFilter;
        private ImeTextBox input;
        private Label topState, recordState, comparison, sampleHint, coverageLabel, footer;
        private Button startButton, stopButton, pauseButton, nextButton;
        private CheckedListBox releaseKeys, pressKeys;
        private NumericUpDown releaseWindow, pressWindow;
        private CheckBox enableCorrection;
        private NotifyIcon tray;
        private ToolStripMenuItem trayStatus, trayPause;
        private System.Windows.Forms.Timer pulse;
        private bool exiting, changing, startHidden, textDirty, savingTrial;
        private volatile bool inputFocused;
        private Sample currentSample;
        private long trialFirstSequence;
        private long trialHookKeyDownsAtStart;
        private int trialTextActivity;
        private DateTime lastStatusWrite = DateTime.MinValue;
        private string lastRuntimeSignature = "";
        private const int MaxInputLength = 2000;
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
        public MainForm(bool hidden)
        {
            startHidden = hidden; settings = AppStorage.LoadSettings();
            Text = "타이핑튠 (TypingTune) · 한글 입력 진단";
            Name = "TypingTuneMainWindow"; AccessibleName = Text;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            Font = new Font("맑은 고딕", 10F); BackColor = Color.FromArgb(14, 22, 34); ForeColor = Ink;
            ClientSize = new Size(1180, 880); MinimumSize = new Size(1040, 780); StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BuildLayout();
            wheelFilter = new HoverWheelFilter(this); Application.AddMessageFilter(wheelFilter);
            monitor = new NativeKeyboardMonitor(store, delegate { return inputFocused; });
            try { monitor.ApplyProfile(settings.Profile ?? GuardProfile.Default()); }
            catch { settings.Profile = GuardProfile.Default(); monitor.ApplyProfile(settings.Profile); }
            ReflectSettings(); BuildTray();
            input.OnImeEvent = delegate(string type, Dictionary<string, object> details) {
                if (store.Recording) store.Add("win32-ime", type, details);
            };
            input.Enter += delegate { inputFocused = true; if (!store.Recording && input.Text.Length == 0) StartRecording(); store.Add("ui", "input-focus", D("focused", true)); };
            input.Leave += delegate { inputFocused = false; store.Add("ui", "input-blur", D("focused", false)); };
            Deactivate += delegate { inputFocused = false; };
            Activated += delegate { inputFocused = input.Focused; };
            input.TextChanged += delegate {
                if (changing) return;
                textDirty = true;
                if (store.Recording) trialTextActivity++;
                store.Add("ui", "text-changed", new Dictionary<string, object> {
                    { "text", input.Text }, { "selectionStart", input.SelectionStart }, { "selectionLength", input.SelectionLength },
                    { "isComposing", input.Composing }, { "lengthUnit", "UTF-16 code units" }, { "trialId", currentSample == null ? "" : currentSample.Id }
                });
            };
            pulse = new System.Windows.Forms.Timer { Interval = 300 };
            pulse.Tick += delegate { TickState(); }; pulse.Start();
            sampleChoice.SelectedIndex = 0;
            FormClosing += OnClosing;
            Shown += delegate {
                // Foreground Raw Input registration prevented low-level hook delivery
                // in the reproduced 2.0.0 failure. Keep this window on the hook/IME path.
                if (startHidden) { Hide(); ShowInTaskbar = false; }
                TickState();
            };
        }
        private static Dictionary<string, object> D(string key, object value) { return new Dictionary<string, object> { { key, value } }; }
        private Label LabelText(string text, float size, Color color)
        { return new Label { Text = text, Font = new Font("맑은 고딕", size), ForeColor = color, AutoSize = true, Margin = new Padding(0, 4, 12, 4) }; }
        private Button ActionButton(string text, EventHandler click, bool primary)
        {
            Button b = new Button { Text = text, AutoSize = true, Height = 38, MinimumSize = new Size(98, 38), Padding = new Padding(12, 4, 12, 4), FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(28, 104, 99) : PanelColor, ForeColor = Ink, Margin = new Padding(0, 3, 8, 3), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(57, 75, 97); b.Click += click; return b;
        }
        private TableLayoutPanel Table(int rows)
        { return new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, Padding = new Padding(18), BackColor = BackColor, Margin = Padding.Empty }; }
        private TextBox TextField(bool readOnly)
        { return new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = readOnly, ScrollBars = ScrollBars.Vertical, BackColor = PanelColor, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle, Font = new Font("맑은 고딕", 13F), Margin = new Padding(0, 4, 0, 6) }; }
        private void BuildLayout()
        {
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
            Panel header = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 10, 22, 0) };
            Label title = LabelText("TypingTune  타이핑튠", 18, Ink); title.Location = new Point(22, 6);
            Label subtitle = LabelText("한글 입력을 기록하고, 문제 신호를 찾아 조율합니다.", 10, Muted); subtitle.Location = new Point(25, 48);
            topState = LabelText("진단 준비", 11, Accent); topState.AutoSize = false; topState.TextAlign = ContentAlignment.MiddleRight; topState.Dock = DockStyle.Right; topState.Width = 390;
            header.Controls.Add(title); header.Controls.Add(subtitle); header.Controls.Add(topState); root.Controls.Add(header, 0, 0);
            tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(22, 8), Font = new Font("맑은 고딕", 10F) };
            TabPage diagnosis = new TabPage("입력 진단") { BackColor = BackColor };
            TabPage correction = new TabPage("보정 설정") { BackColor = BackColor };
            TabPage guide = new TabPage("진단 JSON 안내") { BackColor = BackColor };
            tabs.TabPages.AddRange(new[] { diagnosis, correction, guide }); root.Controls.Add(tabs, 0, 1);
            footer = LabelText("검사 입력칸 안에서만 일반 키와 입력 문장을 기록합니다. 자동 전송은 없습니다.", 9, Muted); footer.Dock = DockStyle.Fill; footer.Padding = new Padding(18, 0, 0, 0); root.Controls.Add(footer, 0, 2);
            Controls.Add(root); BuildDiagnosis(diagnosis); BuildCorrection(correction); BuildGuide(guide);
        }
        private void BuildDiagnosis(TabPage page)
        {
            var layout = Table(10); diagnosisLayout = layout; layout.Dock = DockStyle.Top;
            diagnosisViewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            page.Controls.Add(diagnosisViewport);
            diagnosisViewport.ClientSizeChanged += delegate { FitExpectedText(); };
            foreach (float h in new float[] { 35, 44, 31, 106, 42, 28 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
            diagnosisViewport.Controls.Add(layout);
            layout.Controls.Add(LabelText("01  예문 선택 → 02  직접 입력 → 03  JSON 저장 · 분석", 12, Accent), 0, 0);
            TableLayoutPanel choose = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            choose.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72)); choose.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            sampleChoice = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("맑은 고딕", 11), DropDownWidth = 700, Margin = new Padding(0, 4, 12, 4) };
            foreach (Sample s in samples) sampleChoice.Items.Add(s);
            sampleChoice.SelectedIndexChanged += delegate { ChangeSample(); };
            keyboardChoice = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 0, 4) };
            keyboardChoice.Items.AddRange(new object[] { "노트북 내장 키보드", "USB 외장 키보드", "Bluetooth 키보드", "화상 키보드", "기타 / 알 수 없음" });
            keyboardChoice.SelectedIndex = Math.Max(0, keyboardChoice.Items.IndexOf(settings.KeyboardLabel));
            keyboardChoice.SelectedIndexChanged += delegate { settings.KeyboardLabel = keyboardChoice.Text; SaveSettings(); store.Add("ui", "keyboard-label-changed", D("userProvidedLabel", keyboardChoice.Text)); };
            choose.Controls.Add(sampleChoice, 0, 0); choose.Controls.Add(keyboardChoice, 1, 0); layout.Controls.Add(choose, 0, 1);
            sampleHint = LabelText("예문", 9, Muted); sampleHint.Dock = DockStyle.Fill; layout.Controls.Add(sampleHint, 0, 2);
            expected = new Label { Dock = DockStyle.Fill, AutoSize = false, UseMnemonic = false,
                BackColor = PanelColor, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("맑은 고딕", 13F), Padding = new Padding(6), Margin = new Padding(0, 4, 0, 6),
                AccessibleName = "따라 입력할 예문 · 전체 문장", TabStop = false };
            layout.Controls.Add(expected, 0, 3);
            expected.SizeChanged += delegate { FitExpectedText(); };
            expected.FontChanged += delegate { FitExpectedText(); };
            expected.TextChanged += delegate { FitExpectedText(); };
            FlowLayoutPanel nav = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            nav.Controls.Add(ActionButton("이전 예문", delegate { if (sampleChoice.SelectedIndex > 0) sampleChoice.SelectedIndex--; }, false));
            nextButton = ActionButton("검사 완료 · 다음", delegate { CompleteAndSaveNext(); }, false); nav.Controls.Add(nextButton);
            coverageLabel = LabelText("완료 · 다음 → 다운로드 폴더에 JSON 자동 저장", 9, Muted); nav.Controls.Add(coverageLabel); layout.Controls.Add(nav, 0, 4);
            layout.Controls.Add(LabelText("아래 칸에 직접 입력하세요. 붙여넣기는 실제 키보드 검사와 구분되어 기록됩니다.", 10, Ink), 0, 5);
            input = new ImeTextBox { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, AcceptsTab = false, MaxLength = MaxInputLength, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(10, 17, 27), ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle, Font = new Font("맑은 고딕", 16), Margin = new Padding(0, 4, 0, 8), ImeMode = ImeMode.NoControl, AccessibleName = "한글 입력 진단 검사창", Name = "DiagnosticInput" };
            layout.Controls.Add(input, 0, 6);
            comparison = LabelText("입력을 기다리는 중입니다.", 10, Muted); comparison.Dock = DockStyle.Fill; layout.Controls.Add(comparison, 0, 7);
            FlowLayoutPanel keys = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = true, AutoScroll = true };
            string latin = "QWERTYUIOPASDFGHJKLZXCVBNM"; string korean = "ㅂㅈㄷㄱㅅㅛㅕㅑㅐㅔㅁㄴㅇㄹㅎㅗㅓㅏㅣㅋㅌㅊㅍㅠㅜㅡ";
            for (int i = 0; i < latin.Length; i++) AddKeyLabel(keys, latin[i].ToString(), latin[i] + " " + korean[i]);
            foreach (string token in new[] { "R ㄲ", "E ㄸ", "Q ㅃ", "T ㅆ", "W ㅉ", "O ㅒ", "P ㅖ" }) AddKeyLabel(keys, "Shift+" + token[0], "⇧" + token);
            layout.Controls.Add(keys, 0, 8);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
            startButton = ActionButton("기록 시작", delegate { StartRecording(); input.Focus(); }, true);
            stopButton = ActionButton("기록 중지", delegate { store.Add("ui", "recording-stop", new Dictionary<string, object>()); store.Recording = false; TickState(); }, false);
            buttons.Controls.Add(startButton); buttons.Controls.Add(stopButton);
            buttons.Controls.Add(ActionButton("진단 JSON 저장", delegate { ExportJson(); }, true));
            buttons.Controls.Add(ActionButton("입력·기록 초기화", delegate { ResetRecording(); }, false));
            Button force = ActionButton("강제 종료", delegate { Shutdown(true, false); }, false); force.ForeColor = Color.FromArgb(255, 156, 151); buttons.Controls.Add(force);
            buttons.Controls.Add(ActionButton("프로그램 재시작", delegate { Shutdown(false, true); }, false));
            recordState = LabelText("", 9, Accent); recordState.AutoSize = false; recordState.Width = 1000; recordState.Height = 28; buttons.Controls.Add(recordState); layout.Controls.Add(buttons, 0, 9);
        }
        private void FitExpectedText()
        {
            if (expected == null || diagnosisLayout == null || expected.Width < 20) return;
            // Measure the same wrapping Label uses, including padding and border.
            // The sample has no internal viewport; every line remains visible after resizing.
            int preferred = expected.GetPreferredSize(new Size(expected.Width, 0)).Height;
            int height = Math.Max(preferred, expected.Font.Height * 4 + expected.Padding.Vertical + 2) + expected.Margin.Vertical;
            if (Math.Abs(diagnosisLayout.RowStyles[3].Height - height) > 1)
                diagnosisLayout.RowStyles[3].Height = height;
            int fixedHeight = diagnosisLayout.Padding.Vertical;
            foreach (RowStyle row in diagnosisLayout.RowStyles)
                if (row.SizeType == SizeType.Absolute) fixedHeight += (int)Math.Ceiling(row.Height);
            // On small screens scroll the page instead of collapsing the editable input area.
            diagnosisLayout.Height = Math.Max(diagnosisViewport.ClientSize.Height, fixedHeight + expected.Font.Height * 4);
        }
        private void AddKeyLabel(FlowLayoutPanel panel, string code, string text)
        {
            Label key = new Label { Text = text, Width = code.StartsWith("Shift") ? 63 : 45, Height = 27, TextAlign = ContentAlignment.MiddleCenter, BackColor = PanelColor, ForeColor = Muted, Margin = new Padding(0, 0, 4, 4), Font = new Font("맑은 고딕", 8F) };
            keyLabels[code] = key; panel.Controls.Add(key);
        }
        private void BuildCorrection(TabPage page)
        {
            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24) }; page.Controls.Add(scroll);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(8) }; scroll.Controls.Add(layout);
            layout.Controls.Add(LabelText("노트북마다 다르게, 확인된 증상에 맞춰 적용", 18, Ink));
            layout.Controls.Add(LabelText("먼저 보정을 끄고 진단한 뒤, 동일 예문을 보정을 켜고 다시 검사하세요.", 11, Muted));
            Label explanation = LabelText("이 보정은 문자 키 근처에 끼어드는 ‘주입된 다음 곡’ 신호를 제한합니다.\n누락·중복 입력, 모든 종류의 자모 분리, 키보드 하드웨어 고장을 한꺼번에 고치는 기능은 아닙니다.", 10, Muted); layout.Controls.Add(explanation);
            layout.Controls.Add(LabelText("아래 키 선택은 보정의 시간 기준입니다. 입력 진단은 선택 여부와 관계없이 검사칸의 모든 키를 기록합니다.", 10, Muted));
            enableCorrection = new CheckBox { Text = "한글 입력 보정 사용", AutoSize = true, ForeColor = Accent, Font = new Font("맑은 고딕", 12F), Margin = new Padding(0, 16, 0, 14) }; layout.Controls.Add(enableCorrection);
            FlowLayoutPanel lists = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            Panel releasePanel = new Panel { Width = 350, Height = 282 }; Panel pressPanel = new Panel { Width = 350, Height = 282 };
            Label relTitle = LabelText("키를 뗀 직후 검사할 키", 11, Ink); relTitle.Location = new Point(0, 0); releasePanel.Controls.Add(relTitle);
            releaseKeys = new CheckedListBox { Location = new Point(0, 35), Size = new Size(330, 206), CheckOnClick = true, MultiColumn = true, ColumnWidth = 100, BackColor = PanelColor, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle };
            Label pressTitle = LabelText("처음 누른 직후 검사할 키", 11, Ink); pressTitle.Location = new Point(0, 0); pressPanel.Controls.Add(pressTitle);
            pressKeys = new CheckedListBox { Location = new Point(0, 35), Size = new Size(330, 206), CheckOnClick = true, MultiColumn = true, ColumnWidth = 100, BackColor = PanelColor, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle };
            for (int code = 65; code <= 90; code++) { releaseKeys.Items.Add(new KeyOption(code)); pressKeys.Items.Add(new KeyOption(code)); }
            releaseKeys.Items.Add(new KeyOption(0x15)); pressKeys.Items.Add(new KeyOption(8));
            releasePanel.Controls.Add(releaseKeys); pressPanel.Controls.Add(pressKeys);
            releaseWindow = new NumericUpDown { Minimum = 1, Maximum = 250, Value = 150, Location = new Point(0, 248), Width = 80 }; releasePanel.Controls.Add(releaseWindow);
            Label rm = LabelText("ms 이내 (기본 150)", 9, Muted); rm.Location = new Point(90, 250); releasePanel.Controls.Add(rm);
            pressWindow = new NumericUpDown { Minimum = 1, Maximum = 50, Value = 30, Location = new Point(0, 248), Width = 80 }; pressPanel.Controls.Add(pressWindow);
            Label pm = LabelText("ms 이내 · 누른 동안 (기본 30)", 9, Muted); pm.Location = new Point(90, 250); pressPanel.Controls.Add(pm);
            lists.Controls.Add(releasePanel); lists.Controls.Add(pressPanel); layout.Controls.Add(lists);
            FlowLayoutPanel actions = new FlowLayoutPanel { AutoSize = true };
            actions.Controls.Add(ActionButton("설정 적용", delegate { ApplyControls(); }, true));
            actions.Controls.Add(ActionButton("기존 증상 프리셋", delegate { ReflectSettings(GuardProfile.Default()); feedback("T/O/D/Y/한영 해제 및 Q/Backspace 누름 조건을 불러왔습니다. 적용할 때 보정 사용을 선택하세요."); }, false));
            pauseButton = ActionButton("일시 중지", delegate { TogglePause(); }, false); actions.Controls.Add(pauseButton); layout.Controls.Add(actions);
            layout.Controls.Add(LabelText("선택한 문자 키는 그대로 전달하며, 시간 조건에 맞는 특정 ‘주입된 다음 곡’ 신호만 차단합니다.\n모두 선택하면 정상 프로그램이 만든 같은 형태의 신호까지 차단할 가능성이 커집니다.", 10, Muted));
            layout.Controls.Add(LabelText("보정 규칙은 Windows 전체에 적용됩니다. 선택한 키보드 종류는 진단용 표시이며 특정 장치만 제한하지 않습니다.\n설정을 바꾸면 진단 타임라인에 변경 시점과 실제 적용 값이 함께 기록됩니다.", 10, Muted));
        }
        private void BuildGuide(TabPage page)
        {
            TextBox text = TextField(true); text.Font = new Font("맑은 고딕", 12); text.BorderStyle = BorderStyle.None; text.BackColor = BackColor;
            text.AccessibleName = "진단 JSON 안내 · 마우스 휠로 스크롤";
            text.Text = "진단 JSON을 GPT 또는 다른 LLM에 전달할 수 있습니다.\r\n\r\n" +
                "1. 입력 진단 탭에서 키보드 종류와 예문을 선택하고 직접 입력합니다.\r\n2. ‘검사 완료 · 다음’을 누르면 다운로드 폴더에 JSON을 자동 저장한 뒤 다음 예문으로 넘어갑니다.\r\n3. 마지막 예문도 같은 버튼으로 저장합니다. ‘진단 JSON 저장’으로 직접 저장 위치를 고를 수도 있습니다.\r\n4. 저장한 파일을 LLM에 첨부하고, 아래 요청을 함께 전달하세요.\r\n\r\n" +
                "자동 저장\r\n• 매번 다른 파일 이름으로 저장하며, 완료한 예문과 입력·이벤트·보정 상태 및 이전 완료 검사를 함께 담습니다.\r\n• 글자가 입력되지 않은 검사도 저장합니다. 빈 결과만으로 키보드가 정상이라고 판단하지 않습니다.\r\n• 저장에 실패하면 다음 예문으로 넘어가지 않고 현재 입력과 기록을 유지합니다.\r\n• 예문 선택 목록이나 ‘이전 예문’은 자동 저장을 실행하지 않습니다.\r\n\r\n" +
                "“TypingTune 진단 JSON을 분석해 주세요. 정상 키·IME 조합과 이상 신호를 구분하고, 이벤트 순서와 시간 근거를 제시해 주세요. 실제 확인된 사실과 가설을 분리하고, 누락된 기록과 보정 활성 여부를 고려하세요. 먼저 되돌릴 수 있는 해결책과 추가 A/B 검사를 제안해 주세요.”\r\n\r\n" +
                "JSON에 포함되는 내용\r\n• 예문과 실제 입력, 유니코드 조합, 비교 결과와 검사 범위\r\n• 검사칸의 키 이벤트, IME 조합 과정, 입력 변화와 선택 위치\r\n• 키보드 훅의 주입 플래그, 미디어 입력의 시간 관계 및 차단 여부\r\n• 보정 설정, 실행 환경, 사용자가 선택한 키보드 종류\r\n• 보관 한도 3,000개, 버려진 이벤트 수, 입력 수집 상태와 해석상 한계\r\n\r\n" +
                "보정 설정의 키 목록\r\n• 선택한 키를 누르거나 뗀 시점을 이상 신호의 시간 기준으로 사용합니다. 문자 키 자체를 차단하지 않습니다.\r\n• 입력 진단은 선택 여부와 관계없이 검사칸의 모든 키를 기록합니다.\r\n• 모두 선택하면 정상 프로그램이 만든 같은 형태의 ‘다음 곡’ 신호까지 차단할 가능성이 커집니다.\r\n• 보정은 Windows 전체에 적용되며, 내장 키보드만 자동 식별해 적용하는 기능은 아닙니다.\r\n\r\n" +
                "개인정보와 진단의 한계\r\n• 일반 키와 문장은 검사칸 안에서 기록합니다. 클립보드를 직접 읽거나 인터넷으로 전송하지 않습니다.\r\n• 저장한 JSON에는 직접 입력한 문장이 들어갑니다. 공유 전 내용을 확인하세요.\r\n• 자동 입력이나 붙여넣기로는 키보드의 물리적인 정상 동작을 증명할 수 없습니다.\r\n• 주입 플래그만으로 신호를 만든 프로그램이나 하드웨어 원인을 특정할 수 없습니다.\r\n• 키보드 종류는 사용자가 선택한 설명입니다. 현재 수집 방식으로 개별 물리 장치를 자동 구별하지 않습니다.\r\n\r\n" +
                "종료와 재시작\r\n• 창의 X 버튼은 알림 영역으로 숨깁니다. 진단 기록은 중지됩니다.\r\n• 알림 영역 메뉴에서 작동 상태, 차단 횟수, 일시 중지 및 종료를 제어합니다.\r\n• 강제 종료는 TypingTune 프로세스를 종료합니다.\r\n• 프로그램 재시작은 기존 프로세스가 완전히 끝난 뒤 새 프로세스로 실행합니다.\r\n• 종료·재시작 시 미저장 진단이 있으면 로컬 데이터 폴더의 recovery 폴더에 보관합니다.\r\n\r\n" +
                "지원: Windows 10/11, .NET Framework 4.8 이상. 다른 노트북에서는 보정을 끈 진단부터 시작하세요.";
            Panel wrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(26) }; wrap.Controls.Add(text); page.Controls.Add(wrap);
        }
        private sealed class KeyOption
        {
            public int Code; public KeyOption(int c) { Code = c; }
            public override string ToString() {
                if (Code == 8) return "Backspace"; if (Code == 0x15) return "한/영";
                const string letters = "QWERTYUIOPASDFGHJKLZXCVBNM", jamo = "ㅂㅈㄷㄱㅅㅛㅕㅑㅐㅔㅁㄴㅇㄹㅎㅗㅓㅏㅣㅋㅌㅊㅍㅠㅜㅡ";
                int i = letters.IndexOf((char)Code); return ((char)Code) + (i >= 0 ? " · " + jamo[i] : "");
            }
        }
        private void ReflectSettings(GuardProfile preview = null)
        {
            GuardProfile p = preview ?? settings.Profile ?? GuardProfile.Default(); enableCorrection.Checked = p.Enabled;
            releaseWindow.Value = Math.Max(releaseWindow.Minimum, Math.Min(releaseWindow.Maximum, p.ReleaseWindowMs));
            pressWindow.Value = Math.Max(pressWindow.Minimum, Math.Min(pressWindow.Maximum, p.PressWindowMs));
            for (int i = 0; i < releaseKeys.Items.Count; i++) releaseKeys.SetItemChecked(i, (p.ReleaseKeys ?? new int[0]).Contains(((KeyOption)releaseKeys.Items[i]).Code));
            for (int i = 0; i < pressKeys.Items.Count; i++) pressKeys.SetItemChecked(i, (p.PressKeys ?? new int[0]).Contains(((KeyOption)pressKeys.Items[i]).Code));
        }
        private void ApplyControls()
        {
            GuardProfile profile = new GuardProfile { Name = "사용자 설정", Enabled = enableCorrection.Checked, ReleaseKeys = releaseKeys.CheckedItems.Cast<KeyOption>().Select(k => k.Code).ToArray(), PressKeys = pressKeys.CheckedItems.Cast<KeyOption>().Select(k => k.Code).ToArray(), ReleaseWindowMs = (int)releaseWindow.Value, PressWindowMs = (int)pressWindow.Value };
            monitor.ApplyProfile(profile); settings.Profile = profile; SaveSettings();
            store.Add("ui", "profile-changed", D("profile", profile.ToDictionary())); TickState(); feedback("보정 설정을 적용했습니다.");
        }
        private void SaveSettings() { AppStorage.Save(Path.Combine(AppStorage.DataDirectory, "settings.json"), settings); }
        private void StartRecording()
        {
            if (store.Recording) return;
            store.Recording = true; store.Add("ui", "recording-start", new Dictionary<string, object> { { "guard", monitor == null ? null : monitor.SnapshotSummary() }, { "sampleId", currentSample == null ? "" : currentSample.Id } });
            if (currentSample != null) store.Add("ui", "trial-context", new Dictionary<string, object> { { "sampleId", currentSample.Id }, { "expected", currentSample.Text }, { "firstSequence", trialFirstSequence }, { "guard", monitor.SnapshotSummary() } });
            TickState();
        }
        private void ChangeSample()
        {
            if (sampleChoice.SelectedIndex < 0) return;
            CompleteTrial(); currentSample = samples[sampleChoice.SelectedIndex];
            changing = true; input.Clear(); expected.Text = currentSample.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"); changing = false;
            observedKeys.Clear(); monitor.ClearDiagnosticKeySlots(); sampleHint.Text = currentSample.Description;
            trialHookKeyDownsAtStart = monitor.RecordedKeyDowns; trialTextActivity = 0;
            trialFirstSequence = store.Total + 1;
            store.Add("ui", "trial-start", new Dictionary<string, object> { { "sampleId", currentSample.Id }, { "expected", currentSample.Text }, { "category", currentSample.Category } });
            textDirty = true; TickState();
        }
        private void CompleteTrial()
        {
            if (currentSample == null || input.Text.Length == 0) return;
            CommitCompletedTrial(SnapshotCompletedTrial());
        }
        private Dictionary<string, object> SnapshotCompletedTrial()
        {
            foreach (string keySlot in monitor.DiagnosticKeySlotsSnapshot()) observedKeys.Add(keySlot);
            return new Dictionary<string, object> {
                { "sampleId", currentSample.Id }, { "title", currentSample.Title }, { "expectedText", currentSample.Text }, { "actualText", input.Text },
                { "completedAtUtc", DateTime.UtcNow.ToString("o") }, { "firstSequence", trialFirstSequence }, { "lastSequence", store.Total },
                { "observedUiKeySlots", observedKeys.ToArray() }, { "guardAtCompletion", monitor.SnapshotSummary() },
                { "analysis", DiagnosticExporter.Analyze(currentSample.Text, input.Text, store.Snapshot().Where(e => Convert.ToInt64(e["seq"]) >= trialFirstSequence).ToList()) }
            };
        }
        private void CommitCompletedTrial(Dictionary<string, object> trial)
        {
            trials.Add(trial); store.Add("ui", "trial-complete", D("sampleId", currentSample.Id));
            changing = true; input.Clear(); changing = false;
            trialFirstSequence = store.Total + 1; observedKeys.Clear(); monitor.ClearDiagnosticKeySlots(); textDirty = true;
            trialHookKeyDownsAtStart = monitor.RecordedKeyDowns; trialTextActivity = 0;
        }
        private void CompleteAndSaveNext()
        {
            if (savingTrial || currentSample == null) return;
            savingTrial = true; nextButton.Enabled = false;
            try
            {
                string path;
                Dictionary<string, object> trial;
                try
                {
                    // Stage completion without clearing input or mutating trial history.
                    // The saved report still points to the sample the user just finished.
                    trial = SnapshotCompletedTrial();
                    var completed = new List<Dictionary<string, object>>(trials); completed.Add(trial);
                    var report = DiagnosticExporter.Build(store, monitor, currentSample, input.Text, Context(), completed);
                    report["automaticSave"] = new Dictionary<string, object> {
                        { "trigger", "complete-and-next" }, { "completedSampleId", currentSample.Id },
                        { "completedTrialCount", completed.Count },
                        { "currentTrialAlsoStoredAtCompletedIndex", completed.Count - 1 },
                        { "scope", "Saved before clearing input or moving to the next sample. currentTrial and the last completedTrials entry describe the same attempt; do not count them twice. Files from the same session can overlap: use session.id and event seq to avoid counting repeated events." }
                    };
                    path = DiagnosticExporter.SaveNewToDownloads(report, currentSample.Id);
                }
                catch (Exception ex)
                {
                    feedback("자동 저장 실패 · 입력과 기록을 유지했습니다. 다운로드 폴더를 확인하거나 ‘진단 JSON 저장’을 이용하세요.");
                    MessageBox.Show(this, "다운로드 폴더에 JSON을 저장하지 못했습니다.\n현재 입력과 기록을 유지했으며 다음 예문으로 넘어가지 않았습니다.\n\n" + ex.Message,
                        "타이핑튠 · 자동 저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                CommitCompletedTrial(trial);
                bool hasNext = sampleChoice.SelectedIndex < samples.Count - 1;
                if (hasNext) sampleChoice.SelectedIndex++;
                TickState();
                feedback((hasNext ? "다운로드 폴더에 자동 저장했습니다: " : "마지막 예문까지 자동 저장했습니다: ") + Path.GetFileName(path));
                if (hasNext) input.Focus();
            }
            finally { savingTrial = false; nextButton.Enabled = true; }
        }
        private void ResetRecording()
        {
            store.Recording = false; changing = true; input.Clear(); changing = false;
            trials.Clear(); observedKeys.Clear(); monitor.ClearDiagnosticKeySlots(); store.Clear(); trialFirstSequence = 1; textDirty = true;
            trialHookKeyDownsAtStart = monitor.RecordedKeyDowns; trialTextActivity = 0; TickState();
        }
        private Dictionary<string, object> Context()
        {
            string manufacturer = "", model = "", osBuild = Environment.OSVersion.Version.ToString();
            try { using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("HARDWARE\\DESCRIPTION\\System\\BIOS")) { if (k != null) { manufacturer = Convert.ToString(k.GetValue("SystemManufacturer")); model = Convert.ToString(k.GetValue("SystemProductName")); } } } catch { }
            try { using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion")) { if (k != null) osBuild = Convert.ToString(k.GetValue("CurrentBuildNumber")) + "." + Convert.ToString(k.GetValue("UBR")); } } catch { }
            return new Dictionary<string, object> {
                { "appVersion", Program.Version }, { "windowsBuild", osBuild }, { "manufacturer", manufacturer }, { "model", model },
                { "os64Bit", Environment.Is64BitOperatingSystem }, { "process64Bit", Environment.Is64BitProcess }, { "dotNet", Environment.Version.ToString() },
                { "keyboardUserLabel", keyboardChoice.Text }, { "layoutUserLabel", settings.LayoutLabel }, { "hklAtExport", GetKeyboardLayout(0).ToInt64().ToString("X") },
                { "inputLanguage", InputLanguage.CurrentInputLanguage.Culture.Name }, { "inputLimit", MaxInputLength },
                { "observedUiKeySlotsCurrentTrial", observedKeys.ToArray() }, { "currentTrialFirstSequence", trialFirstSequence },
                { "legacyGuardRunning", Process.GetProcessesByName("ImeMediaGuard").Length > 0 },
                { "deviceIdentificationLimit", "Keyboard selection is a user-provided label, not native per-device attribution. Default capture uses low-level hook and IME; Raw Input is intentionally not registered. Manufacturer and model only; no serial, user name, host name, clipboard, file paths, or raw device instance path is exported." }
            };
        }
        private Dictionary<string, object> BuildReport() { return DiagnosticExporter.Build(store, monitor, currentSample, input.Text, Context(), trials); }
        private void ExportJson()
        {
            using (SaveFileDialog dialog = new SaveFileDialog { Title = "TypingTune 진단 JSON 저장", Filter = "진단 JSON (*.json)|*.json", FileName = "TypingTune-진단-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json", DefaultExt = "json", AddExtension = true, InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                DiagnosticExporter.Save(dialog.FileName, BuildReport()); feedback("진단 JSON을 저장했습니다. 입력 문장이 포함되므로 공유 전 확인해 주세요.");
            }
        }
        private void SaveRecovery()
        {
            if (store.Total == 0 && input.Text.Length == 0 && trials.Count == 0) return;
            string folder = Path.Combine(AppStorage.DataDirectory, "recovery"); Directory.CreateDirectory(folder);
            DiagnosticExporter.Save(Path.Combine(folder, "TypingTune-recovery-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json"), BuildReport());
        }
        private void BuildTray()
        {
            var menu = new ContextMenuStrip(); trayStatus = new ToolStripMenuItem("진단 준비") { Enabled = false };
            trayPause = new ToolStripMenuItem("일시 중지", null, delegate { TogglePause(); });
            menu.Items.Add(new ToolStripMenuItem("입력 진단 열기", null, delegate { ShowDiagnosis(); })); menu.Items.Add(trayStatus); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(trayPause);
            menu.Items.Add(new ToolStripMenuItem("종료", null, delegate { Shutdown(false, false); }));
            tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "타이핑튠 · 입력 진단", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { ShowDiagnosis(); };
        }
        private void TogglePause() { monitor.SetPaused(!monitor.Paused); store.Add("ui", monitor.Paused ? "pause" : "resume", D("guard", monitor.SnapshotSummary())); TickState(); }
        private void ShowDiagnosis() { tabs.SelectedIndex = 0; ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; Activate(); }
        private void feedback(string text) { footer.Text = text; }
        private void TickState()
        {
            if (monitor == null || exiting) return;
            if (Program.ShowEvent != null && Program.ShowEvent.WaitOne(0)) ShowDiagnosis();
            if (Program.ExitEvent != null && Program.ExitEvent.WaitOne(0)) { Shutdown(false, false); return; }
            long trialKeys = Math.Max(0L, monitor.RecordedKeyDowns - trialHookKeyDownsAtStart);
            bool captureMissing = trialTextActivity >= 3 && trialKeys == 0;
            string status = !monitor.HookInstalled || monitor.Error != null ? "수집 오류" : captureMissing ? "입력 수집 불완전" : monitor.Paused ? "일시 중지" : monitor.Profile.Enabled ? (monitor.TotalHookEvents == 0 ? "보정 켜짐 · 입력 대기" : "작동 중") : "진단 모드 · 보정 꺼짐";
            string counter = "차단 " + monitor.BlockedPresses + "회 (이벤트 " + monitor.BlockedEvents + "개)";
            topState.Text = status + "\n" + counter; trayStatus.Text = status + " · " + counter;
            trayPause.Text = monitor.Paused ? "보정 재개" : "일시 중지"; pauseButton.Text = trayPause.Text;
            tray.Text = "타이핑튠 · " + status + " · " + monitor.BlockedPresses + "회";
            startButton.Enabled = !store.Recording; stopButton.Enabled = store.Recording;
            recordState.Text = captureMissing ? "⚠ 수집 불완전 · 글자 변화는 있으나 키 이벤트가 없습니다. 붙여넣기 또는 수집 상태를 확인하세요." : (store.Recording ? "● 기록 중" : "대기 / 기록 중지") + " · 키 누름 " + trialKeys + "개 · 보관 " + store.RetainedCount.ToString("N0") + "/3,000 · 누락 " + store.Dropped.ToString("N0") + " · 완료 " + trials.Count;
            recordState.ForeColor = captureMissing ? Color.FromArgb(255, 192, 101) : Accent;
            foreach (string keySlot in monitor.DiagnosticKeySlotsSnapshot()) observedKeys.Add(keySlot);
            foreach (var entry in keyLabels) { bool seen = observedKeys.Contains(entry.Key); entry.Value.BackColor = seen ? Color.FromArgb(27, 100, 79) : PanelColor; entry.Value.ForeColor = seen ? Ink : Muted; }
            if (textDirty && currentSample != null)
            {
                string actual = input.Text.Replace("\r\n", "\n"); string target = currentSample.Text.Replace("\r\n", "\n");
                int jamo = Regex.Matches(actual, "[\\u1100-\\u11FF\\u3130-\\u318F]").Count;
                comparison.Text = actual.Length == 0 ? "입력을 기다리는 중입니다. 초록색 키 표시는 이번 예문에서 관찰한 키입니다." : actual == target ? "✓ 예문과 정확히 일치합니다. 다음 예문으로 진행할 수 있습니다." : "입력 " + actual.Length + "자 / 예문 " + target.Length + "자 · 독립 자모 " + jamo + "개 (조합 중이거나 자모 예문이면 정상일 수 있습니다.)";
                comparison.ForeColor = actual == target && actual.Length > 0 ? Accent : Muted; textDirty = false;
            }
            string signature = status + "/" + counter + "/" + monitor.TotalHookEvents + "/" + Visible + "/" + store.Recording;
            double statusAge = (DateTime.UtcNow - lastStatusWrite).TotalSeconds;
            if ((signature != lastRuntimeSignature && statusAge >= 1) || statusAge >= 10) { WriteRuntimeStatus("running"); lastRuntimeSignature = signature; lastStatusWrite = DateTime.UtcNow; }
        }
        private void WriteRuntimeStatus(string state)
        {
            AppStorage.Save(Path.Combine(AppStorage.DataDirectory, "status.json"), new { app = "TypingTune", version = Program.Version, pid = Process.GetCurrentProcess().Id, state = state, updatedUtc = DateTime.UtcNow.ToString("o"), windowVisible = Visible, recording = store.Recording, guard = monitor.SnapshotSummary() });
        }
        private void Shutdown(bool force, bool restart)
        {
            if (exiting) return;
            store.Add("ui", restart ? "restart-request" : force ? "force-exit-request" : "exit-request", new Dictionary<string, object>());
            inputFocused = false; store.Recording = false;
            try { SaveRecovery(); } catch (Exception ex) { Debug.WriteLine("Recovery save failed: " + ex.Message); }
            try { SaveSettings(); } catch (Exception ex) { Debug.WriteLine("Settings save failed: " + ex.Message); }
            if (restart)
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--wait-for-exit " + Process.GetCurrentProcess().Id + " --data-dir \"" + AppStorage.DataDirectory + "\"") { UseShellExecute = false, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory, WindowStyle = ProcessWindowStyle.Normal });
            }
            exiting = true; pulse.Stop(); monitor.Dispose();
            try { WriteRuntimeStatus("stopped"); } catch (Exception ex) { Debug.WriteLine("Exit status save failed: " + ex.Message); }
            tray.Visible = false; tray.Dispose();
            if (force) { Process.GetCurrentProcess().Kill(); return; }
            Close(); Application.ExitThread();
        }
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (exiting) return;
            if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing) { Shutdown(false, false); return; }
            e.Cancel = true; store.Add("ui", "window-hidden", new Dictionary<string, object>()); inputFocused = false; store.Recording = false; Hide(); ShowInTaskbar = false;
            tray.ShowBalloonTip(1800, "타이핑튠", "알림 영역에서 보정 상태를 확인하거나 완전히 종료할 수 있습니다.", ToolTipIcon.Info);
        }
        protected override void WndProc(ref Message m)
        { if (m.Msg == 0xFF && monitor != null) monitor.HandleRawInput(m.LParam); base.WndProc(ref m); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (wheelFilter != null) Application.RemoveMessageFilter(wheelFilter); if (pulse != null) pulse.Dispose(); if (monitor != null) monitor.Dispose(); if (tray != null) tray.Dispose(); }
            base.Dispose(disposing);
        }
    }
    // Route only this form's wheel messages. Never move keyboard focus or register Raw Input.
    internal sealed class HoverWheelFilter : IMessageFilter
    {
        private readonly Form owner;
        private Control lastTarget;
        private int remainder;
        private const int WheelMessage = 0x020A;
        [StructLayout(LayoutKind.Sequential)] private struct ScrollInfo
        { public uint Size, Mask; public int Min, Max; public uint Page; public int Position, TrackPosition; }
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool GetScrollInfo(IntPtr window, int bar, ref ScrollInfo info);
        public HoverWheelFilter(Form form) { owner = form; }
        private static bool HasScrollRange(Control control, int bar)
        {
            if (!control.IsHandleCreated) return false;
            ScrollInfo info = new ScrollInfo { Size = (uint)Marshal.SizeOf(typeof(ScrollInfo)), Mask = 3 };
            return GetScrollInfo(control.Handle, bar, ref info) && info.Max - info.Min + 1 > info.Page;
        }
        public bool PreFilterMessage(ref Message message)
        {
            if (message.Msg != WheelMessage || owner.IsDisposed || !owner.Visible || Form.ActiveForm != owner) return false;
            long packed = message.LParam.ToInt64();
            Point point = new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
            Control hovered = Control.FromChildHandle(WindowFromPoint(point));
            if (hovered == null || hovered.FindForm() != owner) return false;
            // An open combo list keeps its standard navigation. A closed combo or numeric
            // setting must not change just because the user is scrolling the surrounding page.
            ComboBox combo = hovered as ComboBox;
            if (combo != null && combo.DroppedDown) return false;
            for (Control target = hovered; target != null && target != owner; target = target.Parent)
            {
                TextBoxBase text = target as TextBoxBase;
                if (text != null && text.Multiline && HasScrollRange(text, 1))
                {
                    SendMessage(text.Handle, message.Msg, message.WParam, message.LParam);
                    return true;
                }
                CheckedListBox list = target as CheckedListBox;
                if (list != null && HasScrollRange(list, list.MultiColumn ? 0 : 1))
                {
                    SendMessage(list.Handle, message.Msg, message.WParam, message.LParam);
                    return true;
                }
                ScrollableControl panel = target as ScrollableControl;
                if (panel == null || !panel.AutoScroll || !panel.VerticalScroll.Visible) continue;
                if (lastTarget != panel) { lastTarget = panel; remainder = 0; }
                remainder += unchecked((short)((message.WParam.ToInt64() >> 16) & 0xffff));
                int steps = remainder / 120; remainder %= 120;
                int lines = SystemInformation.MouseWheelScrollLines;
                int distance = lines < 0 ? panel.ClientSize.Height : lines * panel.Font.Height;
                if (steps != 0 && distance != 0)
                    panel.AutoScrollPosition = new Point(-panel.AutoScrollPosition.X, Math.Max(0, -panel.AutoScrollPosition.Y - steps * distance));
                return true;
            }
            return true;
        }
    }
}
