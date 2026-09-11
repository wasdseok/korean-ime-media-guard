using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TypingTune
{
    public sealed class ImeTextBox : TextBox
    {
        public Action<string, Dictionary<string, object>> OnImeEvent;
        public bool Composing { get; private set; }
        [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr hwnd);
        [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr context);
        [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern int ImmGetCompositionStringW(IntPtr context, int index, byte[] buffer, int length);
        private string Composition(int index)
        {
            IntPtr context = ImmGetContext(Handle);
            if (context == IntPtr.Zero) return null;
            try {
                int len = ImmGetCompositionStringW(context, index, null, 0);
                if (len < 0 || len > 65536) return null;
                byte[] data = new byte[len];
                int read = ImmGetCompositionStringW(context, index, data, len);
                return read >= 0 ? Encoding.Unicode.GetString(data, 0, read) : null;
            } finally { ImmReleaseContext(Handle, context); }
        }
        private void Emit(string type, Dictionary<string, object> data)
        { if (OnImeEvent != null) OnImeEvent(type, data); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x10D) { Composing = true; Emit("composition-start", new Dictionary<string, object>()); }
            else if (m.Msg == 0x10F)
            {
                int flags = unchecked((int)m.LParam.ToInt64());
                var fields = new Dictionary<string, object>(); fields["flags"] = flags;
                if ((flags & 8) != 0) fields["composition"] = Composition(8);
                if ((flags & 0x800) != 0) fields["result"] = Composition(0x800);
                fields["selectionStart"] = SelectionStart; fields["selectionLength"] = SelectionLength;
                Emit("composition-update", fields);
            }
            else if (m.Msg == 0x10E) { Composing = false; Emit("composition-end", new Dictionary<string, object> { { "text", Text } }); }
            else if (m.Msg == 0x302) Emit("paste-request", new Dictionary<string, object> { { "note", "Clipboard contents were not accessed by TypingTune. Resulting field text is recorded normally." } });
            else if (m.Msg == 0x51) Emit("input-language-change", new Dictionary<string, object> { { "hkl", m.LParam.ToInt64().ToString("X") } });
            base.WndProc(ref m);
        }
    }
}
