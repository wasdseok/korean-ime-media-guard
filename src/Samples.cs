using System;
using System.Collections.Generic;
using System.Text;

namespace TypingTune
{
    public sealed class Sample
    {
        public string Id;
        public string Title;
        public string Category;
        public string Text;
        public string Description;
        public override string ToString() { return Title; }
    }

    public static class SampleCatalog
    {
        private const string Initials = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
        private const string Medials = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
        private const string Finals = "ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";
        private const string ComplexFinals = "ㄳㄵㄶㄺㄻㄼㄽㄾㄿㅀㅄ";
        private static readonly string[] Shifted = { "Shift+E", "Shift+O", "Shift+P", "Shift+Q", "Shift+R", "Shift+T", "Shift+W" };
        private static readonly Dictionary<char, string> KeyMap = CreateKeyMap();

        public static List<Sample> All()
        {
            return new List<Sample> {
                Make("pangram-keys", "종합 팬그램 · 두벌식 전체 키", "팬그램형 문장",
                    "쌍둥이는 똑똑하게 키보드를 두드리며 삶과 상상을 이야기했다.\n얘는 예쁜 꽃을 보고, 쟤는 하얀 벽 옆에서 빠르게 외쳤다.\n왜 웨이터의 귀여운 표정과 푸른 숲 이야기가 특별한지, 휴일에 짧게 의견을 나눠 봐요.",
                    "세 문장을 합쳐 A–Z 26개 키와 Shift 쌍자음·얘·예 7개 조합을 모두 사용합니다. 두벌식 기준이며, 모든 받침은 별도 받침 검사에 있습니다."),
                Make("initials-19", "초성 19개 · 된소리 포함", "체계 검사",
                    "가 까 나 다 따 라 마 바 빠 사 싸 아 자 짜 차 카 타 파 하",
                    "초성 19개와 ㄲ·ㄸ·ㅃ·ㅆ·ㅉ를 한 번씩 검사합니다."),
                Make("vowels-21", "모음 21개 · 이중 모음 포함", "체계 검사",
                    "아 애 야 얘 어 에 여 예 오 와 왜 외 요 우 워 웨 위 유 으 의 이",
                    "중성 21개를 검사합니다. ㅘ·ㅙ·ㅚ·ㅝ·ㅞ·ㅟ·ㅢ와 Shift+O(얘), Shift+P(예)를 포함합니다."),
                Make("finals-27", "받침 27개 · 모든 종성", "체계 검사",
                    "각 밖 몫 간 앉다 많다 곧 갈 닭 삶 밟다 곬 핥다 읊다 싫다 감 갑 값 갓 갔다 강 낮 낯 부엌 밭 앞 히읗",
                    "현대 한글 종성 27개를 모두 포함합니다. 겹받침 11개와 ㄲ·ㅆ 받침도 포함합니다. 희귀한 받침은 단어 목록으로 검사합니다."),
                Make("complex-finals-11", "겹받침 11개 · 빠짐없이", "체계 검사",
                    "넋 앉다 많다 닭 삶 밟다 곬 핥다 읊다 싫다 값",
                    "순서대로 ㄳ·ㄵ·ㄶ·ㄺ·ㄻ·ㄼ·ㄽ·ㄾ·ㄿ·ㅀ·ㅄ입니다. 곬은 물길을 뜻하는 단어입니다."),
                Make("focus-double", "쌍 · 똑 · 삶 · 상 반복", "집중 반복",
                    "쌍 쌍 쌍 똑 똑 똑 삶 삶 삶 상 상 상\n쌍둥이는 똑똑하게 생각하며 삶의 상상을 펼칩니다.\n삶삶삶 상상상 쌍쌍쌍 똑똑똑",
                    "요청한 쌍·똑·삶·상과 된소리·겹받침·연속 입력 경계를 비교합니다."),
                Make("complex-sentences", "겹받침 문장 · 조합 이어 쓰기", "자연어 문장",
                    "넋을 놓고 앉아 있으니 생각이 많다.\n닭이 마당을 걷고, 삶을 돌아보는 아이는 풀을 밟지 않는다.\n물은 한 곬으로 흐르고 강아지는 앞발을 핥는다.\n시를 읊는 일이 싫지는 않지만 책값은 먼저 확인한다.",
                    "겹받침 11개를 자연스러운 여러 문장 안에서 검사합니다. 다음 모음과 띄어쓰기 전후를 비교하세요."),
                Make("double-shift", "Shift · 쌍자음과 얘·예", "집중 반복",
                    "까 따 빠 싸 짜 얘 예\n깨끗한 꽃과 따뜻한 떡, 빨간 뿌리와 쌀, 짧은 쪽지를 챙겼다.\n얘야, 예쁜 얘기를 쟤에게 전해 줘.",
                    "왼쪽 Shift와 오른쪽 Shift를 따로 사용해 같은 문장을 반복하면 차이를 비교할 수 있습니다."),
                Make("boundaries", "띄어쓰기 · 줄바꿈 · 수정", "문장 경계",
                    "한글 입력이 정상적으로 이어지는지 확인합니다.\n소프트웨어 테스트 키보드 발생.\n삶과 상상, 쌍둥이와 똑똑한 생각을 차례로 적습니다.",
                    "먼저 그대로 입력한 뒤, 별도 시도에서 Backspace로 한 글자를 지우고 다시 입력하세요. 비교할 검사는 JSON으로 각각 저장하세요."),
                Make("free", "자유 입력 · 재현 문장", "자유 입력", "",
                    "키보드 종류와 보정 상태를 확인한 뒤 문제가 생기는 문장을 입력하세요. 자유 입력에는 정답 비교가 없습니다.")
            };
        }

        private static Sample Make(string id, string title, string category, string text, string description)
        {
            return new Sample { Id = id, Title = title, Category = category, Text = text, Description = description };
        }

        private static Dictionary<char, string> CreateKeyMap()
        {
            Dictionary<char, string> result = new Dictionary<char, string>();
            string[] consonantKeys = { "r", "R", "s", "e", "E", "f", "a", "q", "Q", "t", "T", "d", "w", "W", "c", "z", "x", "v", "g" };
            string[] vowelKeys = { "k", "o", "i", "O", "j", "p", "u", "P", "h", "hk", "ho", "hl", "y", "n", "nj", "np", "nl", "b", "m", "ml", "l" };
            string[] finalKeys = { "r", "R", "rt", "s", "sw", "sg", "e", "f", "fr", "fa", "fq", "ft", "fx", "fv", "fg", "a", "q", "qt", "t", "T", "d", "w", "c", "z", "x", "v", "g" };
            for (int i = 0; i < Initials.Length; i++) result[Initials[i]] = consonantKeys[i];
            for (int i = 0; i < Medials.Length; i++) result[Medials[i]] = vowelKeys[i];
            for (int i = 0; i < Finals.Length; i++) result[Finals[i]] = finalKeys[i];
            return result;
        }

        public static Dictionary<string, object> Coverage(string text)
        {
            text = Normalize(text);
            HashSet<char> initials = new HashSet<char>();
            HashSet<char> medials = new HashSet<char>();
            HashSet<char> finals = new HashSet<char>();
            HashSet<string> physical = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> shifted = new HashSet<string>(StringComparer.Ordinal);
            int syllables = 0;
            int standalone = 0;
            foreach (char ch in text)
            {
                int value = ch - 0xAC00;
                if (value >= 0 && value < 11172)
                {
                    syllables++;
                    char leading = Initials[value / 588];
                    char vowel = Medials[(value % 588) / 28];
                    initials.Add(leading); medials.Add(vowel);
                    AddKeys(leading, physical, shifted); AddKeys(vowel, physical, shifted);
                    int tail = value % 28;
                    if (tail > 0) { char final = Finals[tail - 1]; finals.Add(final); AddKeys(final, physical, shifted); }
                }
                else if (KeyMap.ContainsKey(ch))
                {
                    standalone++;
                    AddKeys(ch, physical, shifted);
                }
            }
            string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            List<string> missingPhysical = new List<string>();
            foreach (char letter in alphabet) if (!physical.Contains(letter.ToString())) missingPhysical.Add(letter.ToString());
            List<string> missingShifted = new List<string>();
            foreach (string key in Shifted) if (!shifted.Contains(key)) missingShifted.Add(key);
            return new Dictionary<string, object> {
                { "layout", "Korean standard two-set (두벌식), inferred from text; actual physical key events must be checked separately" },
                { "physicalKeys", Sorted(physical) }, { "physicalKeyCount", physical.Count },
                { "missingPhysicalKeys", missingPhysical },
                { "shiftedKeys", Sorted(shifted) }, { "shiftedKeyCount", shifted.Count },
                { "missingShiftedKeys", missingShifted },
                { "initialJamo", Present(Initials, initials, false) }, { "missingInitialJamo", Present(Initials, initials, true) },
                { "medialJamo", Present(Medials, medials, false) }, { "missingMedialJamo", Present(Medials, medials, true) },
                { "finalJamo", Present(Finals, finals, false) }, { "missingFinalJamo", Present(Finals, finals, true) },
                { "complexFinalJamo", Present(ComplexFinals, finals, false) }, { "missingComplexFinalJamo", Present(ComplexFinals, finals, true) },
                { "syllableCount", syllables }, { "standaloneCompatibilityJamoCount", standalone },
                { "all26PhysicalKeys", missingPhysical.Count == 0 }, { "all7ShiftedKeys", missingShifted.Count == 0 },
                { "all19Initials", initials.Count == 19 }, { "all21Medials", medials.Count == 21 },
                { "all27Finals", finals.Count == 27 }, { "all11ComplexFinals", Present(ComplexFinals, finals, true).Count == 0 },
                { "interpretation", "Text coverage describes intended two-set keystrokes, not proof that each physical key was pressed or works correctly. Initial/medial/final coverage counts composed modern Hangul syllables only." }
            };
        }

        private static string Normalize(string text)
        {
            try { return (text ?? "").Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { return text ?? ""; }
        }

        private static void AddKeys(char jamo, HashSet<string> physical, HashSet<string> shifted)
        {
            string keys;
            if (!KeyMap.TryGetValue(jamo, out keys)) return;
            foreach (char key in keys)
            {
                string name = Char.ToUpperInvariant(key).ToString();
                physical.Add(name);
                if (Char.IsUpper(key)) shifted.Add("Shift+" + name);
            }
        }

        private static List<string> Sorted(HashSet<string> values)
        {
            List<string> result = new List<string>(values); result.Sort(StringComparer.Ordinal); return result;
        }

        private static List<string> Present(string ordered, HashSet<char> found, bool missing)
        {
            List<string> result = new List<string>();
            foreach (char c in ordered) if (found.Contains(c) != missing) result.Add(c.ToString());
            return result;
        }
    }
}
