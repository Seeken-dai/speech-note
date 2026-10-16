using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SpeechNote
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--self-test")
            {
                Environment.ExitCode = SelfTest.Run(args.Length > 1 ? args[1] : "self-test.txt");
                return;
            }
            Application.Run(new PromptWindow(args.FirstOrDefault(), false));
        }
    }

    internal static class Native
    {
        internal const uint Exclude = 0x11;
        internal const int ExStyle = -20, NoActivate = 0x08000000;
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint value);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint value);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] internal static extern IntPtr SendPoint(IntPtr hwnd, int message, IntPtr w, ref Point point);
        [DllImport("user32.dll")] internal static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo info);
        [DllImport("user32.dll")] internal static extern bool GetScrollBarInfo(IntPtr hwnd, int objectId, ref ScrollBarInfo info);
        [StructLayout(LayoutKind.Sequential)]
        internal struct ScrollInfo { public uint Size, Mask; public int Min, Max; public uint Page; public int Position, TrackPosition; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ScrollBarInfo
        {
            public int Size, Left, Top, Right, Bottom, LineButton, ThumbTop, ThumbBottom, Reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public uint[] State;
        }
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] internal static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("user32.dll")] internal static extern bool HideCaret(IntPtr hwnd);
        [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)] private static extern int RtlGetVersion(ref VersionInfo info);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct VersionInfo
        {
            public int Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
        }
        internal static bool SupportsExclusion()
        {
            VersionInfo v = new VersionInfo(); v.Size = Marshal.SizeOf(v);
            return RtlGetVersion(ref v) == 0 && (v.Major > 10 || (v.Major == 10 && v.Build >= 19041));
        }
        internal static void RaiseWithoutFocus(IntPtr hwnd)
        {
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x13); // NOMOVE | NOSIZE | NOACTIVATE
        }
    }

    internal static class ScriptFile
    {
        internal const int MaxBytes = 4 * 1024 * 1024;
        internal static string Read(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".md" && ext != ".txt" && ext != ".markdown")
                throw new InvalidDataException("请选择 MD 或 TXT 文档。");
            byte[] bytes;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length > MaxBytes) throw new InvalidDataException("讲稿超过 4 MB，请拆分后打开。");
                using (MemoryStream data = new MemoryStream())
                {
                    byte[] buffer = new byte[8192]; int count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        if (data.Length + count > MaxBytes) throw new InvalidDataException("讲稿超过 4 MB。");
                        data.Write(buffer, 0, count);
                    }
                    bytes = data.ToArray();
                }
            }
            return Decode(bytes);
        }
        internal static string Decode(byte[] b)
        {
            if (b.Length >= 4 && b[0] == 255 && b[1] == 254 && b[2] == 0 && b[3] == 0) return Encoding.UTF32.GetString(b, 4, b.Length - 4);
            if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 254 && b[3] == 255) return new UTF32Encoding(true, false).GetString(b, 4, b.Length - 4);
            if (b.Length >= 2 && b[0] == 255 && b[1] == 254) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 254 && b[1] == 255) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            int offset = b.Length >= 3 && b[0] == 239 && b[1] == 187 && b[2] == 191 ? 3 : 0;
            try { return new UTF8Encoding(false, true).GetString(b, offset, b.Length - offset); }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(54936).GetString(b); }
        }
    }

    // Render a deliberately small Markdown subset as RTF; never execute HTML or load external resources.
    internal static class Markdown
    {
        private static readonly Regex Tokens = new Regex(@"(\*\*([^*]+)\*\*|__([^_]+)__|`([^`]+)`|\*([^*]+)\*|!?\[([^\]]*)\]\(([^)]*)\))");
        internal static string Escape(string text)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in text)
            {
                if (c == '\\' || c == '{' || c == '}') b.Append('\\').Append(c);
                else if (c == '\t') b.Append(@"\tab ");
                else if (c > 127) b.Append(@"\u").Append((short)c).Append('?');
                else if (c >= 32) b.Append(c);
            }
            return b.ToString();
        }
        private static string Inline(string text)
        {
            StringBuilder b = new StringBuilder(); int last = 0;
            foreach (Match m in Tokens.Matches(text))
            {
                b.Append(Escape(text.Substring(last, m.Index - last)));
                if (m.Groups[2].Success || m.Groups[3].Success) b.Append(@"{\b ").Append(Escape(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value)).Append('}');
                else if (m.Groups[4].Success) b.Append(@"{\f1\cf2 ").Append(Escape(m.Groups[4].Value)).Append('}');
                else if (m.Groups[5].Success) b.Append(@"{\i ").Append(Escape(m.Groups[5].Value)).Append('}');
                else b.Append(Escape((m.Value.StartsWith("!") ? "[图片] " : "") + m.Groups[6].Value));
                last = m.Index + m.Length;
            }
            return b.Append(Escape(text.Substring(last))).ToString();
        }
        internal static string Render(string text, bool markdown, int fontSize, bool dark = false)
        {
            StringBuilder b = new StringBuilder(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Microsoft YaHei UI;}{\f1 Consolas;}}{\colortbl;");
            b.Append(dark ? @"\red255\green255\blue255;\red255\green255\blue255;\red215\green215\blue215;" : @"\red66\green57\blue40;\red158\green89\blue38;\red135\green113\blue77;").Append(@"}\uc1 ");
            bool code = false; char fence = '\0'; int fenceLength = 0;
            foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw; string trimmed = line.TrimStart();
                if (markdown && !code && string.IsNullOrWhiteSpace(line)) continue;
                if (markdown)
                {
                    Match f = Regex.Match(trimmed, @"^(`{3,}|~{3,})(.*)$");
                    if (f.Success && (!code || (f.Groups[1].Value[0] == fence && f.Groups[1].Length >= fenceLength && f.Groups[2].Value.Trim().Length == 0)))
                    {
                        if (!code) { fence = f.Groups[1].Value[0]; fenceLength = f.Groups[1].Length; }
                        code = !code; continue;
                    }
                }
                int size = fontSize * 2; string format = "";
                if (markdown && !code)
                {
                    Match heading = Regex.Match(line, @"^ {0,3}(#{1,6})\s+(.+?)\s*#*\s*$");
                    if (heading.Success)
                    {
                        size += (int)Math.Round(fontSize * Math.Max(2, 14 - heading.Groups[1].Length * 3) / 22.0);
                        format = @"\b\cf2 "; line = heading.Groups[2].Value;
                    }
                    else if (Regex.IsMatch(trimmed, @"^([-*_])(?:\s*\1){2,}\s*$")) line = "────────────────";
                    else if (Regex.IsMatch(trimmed, @"^[-*+]\s+")) line = "•  " + Regex.Replace(trimmed, @"^[-*+]\s+", "");
                    else if (trimmed.StartsWith(">")) { line = trimmed.Substring(1).TrimStart(); format = @"\cf3\i "; }
                }
                b.Append(@"\pard\f0\cf1\b0\i0\sa").Append(Math.Max(30, fontSize * 7)).Append(@"\sl300\slmult1\fs").Append(size).Append(' ').Append(format);
                if (code) b.Append(@"\f1\cf3 ");
                b.Append(markdown && !code ? Inline(line) : Escape(line)).Append(@"\par ");
            }
            return b.Append('}').ToString();
        }
    }

    internal enum NoteIcon { Open, Smaller, Larger, Top, Hide, Read, Moon, Sun, Minimize, Close }

    internal sealed class IconButton : Button
    {
        internal NoteIcon Icon;
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            GraphicsState state = g.Save();
            float side = Math.Min(Width, Height);
            g.TranslateTransform((Width - side) / 2, (Height - side) / 2);
            g.ScaleTransform(side / 24F, side / 24F);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color ink = Enabled ? ForeColor : SystemColors.GrayText;
            using (Pen pen = new Pen(ink, 1.5F))
            using (SolidBrush brush = new SolidBrush(ink))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                switch (Icon)
                {
                    case NoteIcon.Open:
                        g.DrawPolygon(pen, new[] { new PointF(4, 7), new PointF(10, 7), new PointF(12, 9), new PointF(20, 9), new PointF(20, 18), new PointF(4, 18) });
                        g.DrawLine(pen, 4, 11, 20, 11); break;
                    case NoteIcon.Smaller:
                    case NoteIcon.Larger:
                        g.DrawLines(pen, new[] { new PointF(4, 17), new PointF(8, 7), new PointF(12, 17) });
                        g.DrawLine(pen, 6, 13, 10, 13); g.DrawLine(pen, 15, 12, 21, 12);
                        if (Icon == NoteIcon.Larger) g.DrawLine(pen, 18, 9, 18, 15); break;
                    case NoteIcon.Top:
                        g.DrawLine(pen, 5, 5, 19, 5); g.DrawLine(pen, 12, 9, 12, 20);
                        g.DrawLines(pen, new[] { new PointF(7, 13), new PointF(12, 8), new PointF(17, 13) }); break;
                    case NoteIcon.Hide:
                        g.DrawEllipse(pen, 4, 8, 16, 8); g.DrawEllipse(pen, 10, 10, 4, 4); g.DrawLine(pen, 4, 20, 20, 4); break;
                    case NoteIcon.Read:
                        g.DrawRectangle(pen, 6, 4, 12, 16);
                        for (int y = 8; y <= 16; y += 4) g.DrawLine(pen, 9, y, 15, y); break;
                    case NoteIcon.Moon:
                        using (GraphicsPath moon = new GraphicsPath())
                        {
                            moon.AddBezier(15, 4, 1, 4, 1, 21, 15, 20);
                            moon.AddBezier(15, 20, 20, 19, 22, 15, 21, 13);
                            moon.AddBezier(21, 13, 12, 18, 9, 9, 15, 4);
                            g.FillPath(brush, moon);
                        }
                        break;
                    case NoteIcon.Sun:
                        g.DrawEllipse(pen, 8, 8, 8, 8);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = Math.PI * i / 4;
                            g.DrawLine(pen, 12 + (float)Math.Cos(a) * 7, 12 + (float)Math.Sin(a) * 7, 12 + (float)Math.Cos(a) * 10, 12 + (float)Math.Sin(a) * 10);
                        }
                        break;
                    case NoteIcon.Minimize: g.DrawLine(pen, 6, 15, 18, 15); break;
                    case NoteIcon.Close: g.DrawLine(pen, 7, 7, 17, 17); g.DrawLine(pen, 7, 17, 17, 7); break;
                }
            }
            g.Restore(state);
        }
    }

    internal sealed class NoteReader : RichTextBox
    {
        internal event EventHandler ViewChanged;
        private int wheelRemainder;
        internal NoteReader()
        {
            Cursor = Cursors.Arrow; TabStop = false; AutoWordSelection = false;
            SetStyle(ControlStyles.Selectable, false);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x20) // WM_SETCURSOR: RichEdit otherwise supplies its native I-beam.
            {
                Native.SetCursor(Cursors.Arrow.Handle); m.Result = new IntPtr(1); return;
            }
            if (m.Msg == 0x20A)
            {
                // Scroll without requiring RichEdit focus; retain partial ticks from precision wheels.
                wheelRemainder += (short)((m.WParam.ToInt64() >> 16) & 0xffff);
                int ticks = wheelRemainder / 120; wheelRemainder %= 120;
                int lines = SystemInformation.MouseWheelScrollLines;
                if (lines < 0)
                {
                    for (int i = 0; i < Math.Abs(ticks); i++) Native.SendMessage(Handle, 0x115, new IntPtr(ticks > 0 ? 2 : 3), IntPtr.Zero);
                }
                else if (ticks != 0 && lines > 0) Native.SendMessage(Handle, 0xB6, IntPtr.Zero, new IntPtr(-ticks * lines));
                m.Result = IntPtr.Zero; return;
            }
            if (m.Msg == 0x201 || m.Msg == 0x202 || m.Msg == 0x203)
            {
                // Forward note gestures without letting RichEdit focus, place a caret, or select text.
                long point = m.LParam.ToInt64();
                MouseEventArgs args = new MouseEventArgs(MouseButtons.Left, m.Msg == 0x203 ? 2 : 1, (short)(point & 0xffff), (short)((point >> 16) & 0xffff), 0);
                if (m.Msg == 0x201) OnMouseDown(args);
                else if (m.Msg == 0x202) OnMouseUp(args);
                else OnMouseDoubleClick(args);
                m.Result = IntPtr.Zero; return;
            }
            if ((m.Msg == 0x200 && (m.WParam.ToInt64() & 1) != 0) || (m.Msg >= 0x204 && m.Msg <= 0x209) || m.Msg == 0x7B)
            {
                m.Result = IntPtr.Zero; return; // No drag selection, native context menu or middle-button caret.
            }
            if (m.Msg == 0x100)
            {
                // Retain navigation if accessibility or another program explicitly focuses the reader.
                int key = m.WParam.ToInt32();
                int scroll = key == 38 ? 0 : key == 40 ? 1 : key == 33 ? 2 : key == 34 ? 3 : key == 36 ? 6 : key == 35 ? 7 : -1;
                if (scroll >= 0) Native.SendMessage(Handle, 0x115, new IntPtr(scroll), IntPtr.Zero);
                m.Result = IntPtr.Zero; return;
            }
            if (m.Msg == 0x102 || m.Msg == 0x109) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
            if (m.Msg == 7) Native.HideCaret(Handle); // WM_SETFOCUS can still arrive from accessibility clients.
            if (m.Msg == 0x20A || m.Msg == 0x115 || m.Msg == 0xB6 || m.Msg == 0x4DE || m.Msg == 5)
            {
                EventHandler handler = ViewChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }
    }

    internal sealed class NoteScrollBar : Control
    {
        private readonly NoteReader reader;
        private readonly float scale;
        private bool hover, dragging;
        private int grabOffset;
        internal bool Dark;
        internal int Position, Maximum, Page;
        internal Rectangle Thumb { get; private set; }
        internal NoteScrollBar(NoteReader target, float dpiScale)
        {
            reader = target; scale = dpiScale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false; AccessibleRole = AccessibleRole.ScrollBar; AccessibleName = "讲稿滚动条";
            reader.ViewChanged += delegate { RefreshMetrics(); };
            reader.VScroll += delegate { RefreshMetrics(); };
            reader.TextChanged += delegate { RefreshMetrics(); };
            reader.ContentsResized += delegate { RefreshMetrics(); };
        }
        internal void RefreshMetrics()
        {
            if (!reader.IsHandleCreated || reader.IsDisposed) return;
            Native.ScrollInfo info = new Native.ScrollInfo { Size = (uint)Marshal.SizeOf(typeof(Native.ScrollInfo)), Mask = 7 };
            if (!Native.GetScrollInfo(reader.Handle, 1, ref info)) return;
            Maximum = Math.Max(0, info.Max - (int)info.Page + 1); Page = (int)info.Page;
            Native.ScrollBarInfo barInfo = new Native.ScrollBarInfo { Size = Marshal.SizeOf(typeof(Native.ScrollBarInfo)), State = new uint[6] };
            // A forced scrollbar can retain its previous range after a shorter document disables it.
            if (Native.GetScrollBarInfo(reader.Handle, -5, ref barInfo) && (barInfo.State[0] & 1) != 0) Maximum = 0;
            Position = Math.Max(0, Math.Min(Maximum, info.Position));
            int inset = Math.Max(1, (int)(2 * scale)), track = Math.Max(0, Height - inset * 2);
            int thumbHeight = Math.Min(track, Math.Max((int)(22 * scale), (int)(track * (double)Page / Math.Max(1, info.Max + 1))));
            int width = Math.Max(2, (int)((hover || dragging ? 6 : 4) * scale));
            Thumb = Maximum == 0 || track == 0 ? Rectangle.Empty : new Rectangle((Width - width) / 2, inset + (int)((track - thumbHeight) * (double)Position / Maximum), width, thumbHeight);
            Invalidate();
        }
        internal void ScrollToFraction(double fraction)
        {
            RefreshMetrics();
            int position = (int)Math.Round(Math.Max(0, Math.Min(1, fraction)) * Maximum);
            // RichEdit scales very long documents into its native scrollbar range. Use those units,
            // rather than treating the scaled position as a physical pixel offset.
            if (Maximum <= ushort.MaxValue)
                Native.SendMessage(reader.Handle, 0x115, new IntPtr((position << 16) | 4), IntPtr.Zero);
            else
            {
                Point target = new Point(0, position);
                Native.SendPoint(reader.Handle, 0x4DE, IntPtr.Zero, ref target);
            }
            RefreshMetrics();
        }
        private void MoveThumb(int y)
        {
            int inset = Math.Max(1, (int)(2 * scale));
            int travel = Height - inset * 2 - Thumb.Height;
            if (travel > 0) ScrollToFraction((y - grabOffset - inset) / (double)travel);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || Thumb.IsEmpty) return;
            grabOffset = e.Y >= Thumb.Top && e.Y <= Thumb.Bottom ? e.Y - Thumb.Top : Thumb.Height / 2;
            dragging = true; Capture = true; MoveThumb(e.Y);
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) MoveThumb(e.Y); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) { dragging = false; Capture = false; RefreshMetrics(); } }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { dragging = false; RefreshMetrics(); } }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; RefreshMetrics(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; RefreshMetrics(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Native.SendMessage(reader.Handle, 0x20A, new IntPtr(e.Delta << 16), IntPtr.Zero); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); RefreshMetrics(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Thumb.IsEmpty) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color ink = Dark ? (hover || dragging ? Color.FromArgb(173, 173, 173) : Color.FromArgb(103, 103, 103)) : (hover || dragging ? Color.FromArgb(153, 118, 62) : Color.FromArgb(190, 165, 116));
            Rectangle r = Thumb;
            using (GraphicsPath pill = new GraphicsPath())
            using (SolidBrush brush = new SolidBrush(ink))
            {
                pill.AddArc(r.Left, r.Top, r.Width, r.Width, 180, 180);
                pill.AddArc(r.Left, r.Bottom - r.Width, r.Width, r.Width, 0, 180); pill.CloseFigure();
                e.Graphics.FillPath(brush, pill);
            }
        }
    }

    internal sealed class ReaderSurface : Panel
    {
        private readonly NoteReader reader;
        private readonly Panel viewport = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        internal readonly NoteScrollBar Bar;
        internal ReaderSurface(NoteReader target, float scale)
        {
            reader = target; Dock = DockStyle.Fill;
            Bar = new NoteScrollBar(reader, scale) { Dock = DockStyle.Right, Width = (int)Math.Round(14 * scale) };
            Controls.Add(viewport); Controls.Add(Bar); viewport.Controls.Add(reader);
            reader.Dock = DockStyle.None;
            // Keep RichEdit's native range/position calculations, but clip its system scrollbar outside the viewport.
            // The native range also accounts for long documents and variable-height Markdown.
            reader.ScrollBars = RichTextBoxScrollBars.ForcedVertical;
            viewport.SizeChanged += delegate { FitReader(); };
            reader.HandleCreated += delegate { FitReader(); };
        }
        private void FitReader()
        {
            int nativeWidth = reader.IsHandleCreated ? reader.Width - reader.ClientSize.Width : SystemInformation.VerticalScrollBarWidth;
            reader.Bounds = new Rectangle(0, 0, Math.Max(1, viewport.Width + Math.Max(1, nativeWidth)), Math.Max(1, viewport.Height));
            Bar.RefreshMetrics();
        }
        internal void SetReadingOnly(bool reading) { Bar.Visible = !reading; PerformLayout(); FitReader(); }
    }

    internal sealed class Preferences
    {
        internal int Width = 480, Height = 680, FontSize = 22, Alpha = 92;
        internal int Left = int.MinValue, Top = int.MinValue;
        internal int ReadingWidth, ReadingHeight;
        internal bool Dark, ReadingOnly;
        internal static Preferences Read(string path)
        {
            Preferences p = new Preferences();
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] pair = line.Split('='); int n;
                    if (pair.Length != 2 || !int.TryParse(pair[1], out n)) continue;
                    switch (pair[0])
                    {
                        case "Width": p.Width = Math.Max(160, Math.Min(2400, n)); break;
                        case "Height": p.Height = Math.Max(90, Math.Min(2000, n)); break;
                        case "ReadingWidth": p.ReadingWidth = n > 0 ? Math.Max(160, Math.Min(2400, n)) : 0; break;
                        case "ReadingHeight": p.ReadingHeight = n > 0 ? Math.Max(90, Math.Min(2000, n)) : 0; break;
                        case "FontSize": p.FontSize = Math.Max(6, Math.Min(42, n)); break;
                        case "Alpha": p.Alpha = Math.Max(20, Math.Min(100, n)); break;
                        case "Left": p.Left = n; break;
                        case "Top": p.Top = n; break;
                        case "Dark": p.Dark = n == 1; break;
                        case "ReadingOnly": p.ReadingOnly = n == 1; break;
                    }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            return p;
        }
        internal void Save(string path)
        {
            File.WriteAllLines(path, new[] { "Width=" + Width, "Height=" + Height, "FontSize=" + FontSize, "Alpha=" + Alpha, "Left=" + Left, "Top=" + Top, "Dark=" + (Dark ? 1 : 0), "ReadingOnly=" + (ReadingOnly ? 1 : 0), "ReadingWidth=" + ReadingWidth, "ReadingHeight=" + ReadingHeight });
        }
    }

    internal sealed class PromptWindow : Form
    {
        internal readonly NoteReader Reader = new NoteReader();
        internal readonly ReaderSurface ReadingSurface;
        internal readonly TrackBar TransparencySlider = new TrackBar();
        private readonly Label protection = new Label(), fileName = new Label(), alphaLabel = new Label(), sizeLabel = new Label(), hint = new Label();
        private readonly Button hideButton, themeButton;
        private readonly TableLayoutPanel layout;
        private readonly Label title;
        private readonly int[] rowHeights = { 28, 30, 20, 0, 24, 30, 20 };
        private readonly ToolTip tooltips = new ToolTip { ShowAlways = true, InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 8000 };
        private Size readingSize;
        private readonly Timer keeper = new Timer();
        internal readonly NotifyIcon TrayIcon;
        internal readonly ContextMenuStrip TrayMenu;
        private readonly Icon trayArtwork;
        private readonly Preferences prefs;
        private readonly string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
        private readonly bool testing;
        private bool hotkey, modal, ready;
        internal bool Protected { get; private set; }
        internal int FontPoints { get { return prefs.FontSize; } }
        internal bool ReadingOnly { get { return prefs.ReadingOnly; } }
        internal bool Dark { get { return prefs.Dark; } }
        internal readonly float UiScale;
        internal int Px(int logical) { return (int)Math.Round(logical * UiScale); }
        private string script = "# 演讲提词窗\n\n把讲稿留给自己，把目光留给听众。\n\n## 开始使用\n\n点击「打开讲稿」，选择 MD 或 TXT 文件。\n\n- 鼠标移到正文上，滚轮翻阅。\n- 拖动窗口边框，调整大小。\n- 拖动底部滑块，调整透明度。\n\n## 演讲之前\n\n先用钉钉或飞书的观众端确认：共享画面中没有这个窗口。\n\n也可以仅共享 PPT 窗口，进一步避免讲稿进入共享画面。\n\n> 透明度只改变视觉效果，不能保证旁人看不到。";
        private bool markdown = true;
        internal PromptWindow(string initialFile, bool test)
        {
            testing = test; prefs = test ? new Preferences() : Preferences.Read(settingsPath);
            using (Graphics screenGraphics = Graphics.FromHwnd(IntPtr.Zero)) UiScale = screenGraphics.DpiX / 96F;
            Text = "演讲提词窗"; Font = new Font("Microsoft YaHei UI", 9F);
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(255, 246, 217); ForeColor = Color.FromArgb(66, 57, 40);
            FormBorderStyle = FormBorderStyle.None; AutoScaleMode = AutoScaleMode.None;
            MinimumSize = ModeMinimum(prefs.ReadingOnly); Padding = new Padding(Px(7));
            if (prefs.ReadingWidth > 0 && prefs.ReadingHeight > 0) readingSize = new Size(Px(prefs.ReadingWidth), Px(prefs.ReadingHeight));
            Size = new Size(Px(prefs.Width), Px(prefs.Height)); TopMost = true; StartPosition = FormStartPosition.Manual;
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            if (prefs.Left != int.MinValue && prefs.Top != int.MinValue)
            {
                Rectangle candidate = new Rectangle(prefs.Left, prefs.Top, Width, Height);
                Screen screen = Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.IntersectsWith(candidate));
                if (screen != null) work = screen.WorkingArea;
            }
            Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
            Location = new Point(Math.Max(work.Left, Math.Min(prefs.Left == int.MinValue ? work.Right - Width - 32 : prefs.Left, work.Right - Width)),
                Math.Max(work.Top, Math.Min(prefs.Top == int.MinValue ? work.Top + 48 : prefs.Top, work.Bottom - Height)));
            layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(Px(10), Px(6), Px(10), Px(6)) };
            for (int row = 0; row < rowHeights.Length; row++) layout.RowStyles.Add(new RowStyle(row == 3 ? SizeType.Percent : SizeType.Absolute, row == 3 ? 100 : Px(rowHeights[row])));
            Controls.Add(layout);
            Panel header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            title = new Label { Text = "演讲便签", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font.FontFamily, 9F, FontStyle.Bold), ForeColor = Color.FromArgb(158, 89, 38) };
            Button close = MakeButton(NoteIcon.Close, "退出便签", delegate { Close(); }); close.Dock = DockStyle.Right;
            Button minimize = MakeButton(NoteIcon.Minimize, "收起到右下角托盘", delegate { HideToTray(); }); minimize.Dock = DockStyle.Right;
            Button readingButton = MakeButton(NoteIcon.Read, "纯阅读（双击正文切换控制区 / Ctrl + Alt + R）", delegate { SetReadingMode(true); }); readingButton.Dock = DockStyle.Right;
            themeButton = MakeButton(NoteIcon.Moon, "暗黑模式", delegate { SetDarkMode(!prefs.Dark); }); themeButton.Dock = DockStyle.Right;
            header.Controls.Add(title); header.Controls.Add(readingButton); header.Controls.Add(themeButton); header.Controls.Add(minimize); header.Controls.Add(close);
            title.MouseDown += DragNote; header.MouseDown += DragNote;
            layout.Controls.Add(header, 0, 0);
            FlowLayoutPanel toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            toolbar.Controls.Add(MakeButton(NoteIcon.Open, "打开讲稿", delegate { ChooseFile(); }));
            toolbar.Controls.Add(MakeButton(NoteIcon.Smaller, "减小字号（最低 6 号）", delegate { ChangeFont(-1); }));
            sizeLabel.Text = prefs.FontSize.ToString(); sizeLabel.AutoSize = false; sizeLabel.Size = new Size(Px(24), Px(24)); sizeLabel.TextAlign = ContentAlignment.MiddleCenter; sizeLabel.Margin = new Padding(0, 0, Px(2), 0);
            toolbar.Controls.Add(sizeLabel);
            toolbar.Controls.Add(MakeButton(NoteIcon.Larger, "增大字号（最高 42 号）", delegate { ChangeFont(1); }));
            toolbar.Controls.Add(MakeButton(NoteIcon.Top, "回到讲稿顶部", delegate { ScrollToStart(); }));
            hideButton = MakeButton(NoteIcon.Hide, "隐藏便签（单击托盘图标或 Ctrl + Alt + H 恢复）", delegate { ToggleVisibility(); }); toolbar.Controls.Add(hideButton);
            layout.Controls.Add(toolbar, 0, 1);
            fileName.Text = "欢迎使用 · MD / TXT"; fileName.Dock = DockStyle.Fill; fileName.AutoEllipsis = true; fileName.ForeColor = Color.FromArgb(135, 113, 77);
            layout.Controls.Add(fileName, 0, 2);
            Reader.Dock = DockStyle.Fill; Reader.BorderStyle = BorderStyle.None; Reader.BackColor = BackColor; Reader.ForeColor = ForeColor;
            Reader.ReadOnly = true; Reader.DetectUrls = false; Reader.ShortcutsEnabled = false; Reader.HideSelection = true;
            Reader.WordWrap = true; Reader.Visible = false; Reader.TabStop = false;
            ReadingSurface = new ReaderSurface(Reader, UiScale) { Margin = new Padding(Px(2), Px(4), Px(2), Px(4)) };
            layout.Controls.Add(ReadingSurface, 0, 3);
            Reader.MouseDoubleClick += ToggleControlsOnDoubleClick;
            ReadingSurface.MouseDoubleClick += ToggleControlsOnDoubleClick;
            layout.MouseDoubleClick += ToggleControlsOnDoubleClick;
            MouseDoubleClick += ToggleControlsOnDoubleClick;
            Reader.MouseDown += delegate(object sender, MouseEventArgs e) { if (prefs.ReadingOnly && (ModifierKeys & Keys.Alt) != 0) DragNote(sender, e); };
            protection.Dock = DockStyle.Fill; protection.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(protection, 0, 4);
            TableLayoutPanel opacityBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
            opacityBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            opacityBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(54))); opacityBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); opacityBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(46)));
            opacityBar.Controls.Add(new Label { Text = "透明度", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            TransparencySlider.Minimum = 0; TransparencySlider.Maximum = 80; TransparencySlider.Value = 100 - prefs.Alpha;
            TransparencySlider.TickStyle = TickStyle.None; TransparencySlider.SmallChange = 5; TransparencySlider.LargeChange = 10; TransparencySlider.Dock = DockStyle.Fill;
            TransparencySlider.AutoSize = false;
            TransparencySlider.ValueChanged += delegate { ApplyTransparency(100 - TransparencySlider.Value); };
            opacityBar.Controls.Add(TransparencySlider, 1, 0); alphaLabel.Dock = DockStyle.Fill; alphaLabel.TextAlign = ContentAlignment.MiddleRight; opacityBar.Controls.Add(alphaLabel, 2, 0);
            layout.Controls.Add(opacityBar, 0, 5);
            hint.Text = "双击切换控制区 · Alt 拖动"; hint.Dock = DockStyle.Fill; hint.Font = new Font(Font.FontFamily, 8F); hint.ForeColor = fileName.ForeColor;
            tooltips.SetToolTip(hint, "双击正文或 Ctrl + Alt + R 显示／隐藏所有控制按钮；纯阅读时按住 Alt 拖动正文移动便签。");
            layout.Controls.Add(hint, 0, 6);
            trayArtwork = CreateTrayArtwork();
            TrayMenu = new ContextMenuStrip();
            ToolStripMenuItem visibilityItem = new ToolStripMenuItem("隐藏便签", null, delegate { ToggleVisibility(); });
            ToolStripMenuItem controlsItem = new ToolStripMenuItem("隐藏控制区", null, delegate { ShowFromTray(); SetReadingMode(!prefs.ReadingOnly); });
            TrayMenu.Items.Add(visibilityItem);
            TrayMenu.Items.Add(controlsItem);
            TrayMenu.Items.Add(new ToolStripMenuItem("打开讲稿…", null, delegate { ShowFromTray(); ChooseFile(); }));
            TrayMenu.Items.Add(new ToolStripSeparator());
            TrayMenu.Items.Add(new ToolStripMenuItem("退出", null, delegate { Close(); }));
            TrayMenu.Opening += delegate { visibilityItem.Text = Visible ? "隐藏便签" : "显示便签"; controlsItem.Text = prefs.ReadingOnly ? "显示控制区" : "隐藏控制区"; };
            TrayIcon = new NotifyIcon { Icon = trayArtwork, Text = "演讲便签 · 单击显示，右键菜单", ContextMenuStrip = TrayMenu, Visible = !testing };
            TrayIcon.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowFromTray(); };
            keeper.Interval = 800; keeper.Tick += delegate { if (Visible && WindowState != FormWindowState.Minimized && !modal) { EnsureProtection(); Native.RaiseWithoutFocus(Handle); } };
            Shown += delegate
            {
                ready = true; ApplyTransparency(prefs.Alpha); SetDarkMode(prefs.Dark); SetReadingMode(prefs.ReadingOnly);
                if (!string.IsNullOrWhiteSpace(initialFile)) LoadFile(initialFile);
                if (!testing) keeper.Start();
            };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        private void ToggleControlsOnDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Alt) == 0) SetReadingMode(!prefs.ReadingOnly);
        }
        private static Icon CreateTrayArtwork()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                using (SolidBrush paper = new SolidBrush(Color.FromArgb(255, 228, 150)))
                using (Pen ink = new Pen(Color.FromArgb(111, 71, 29), 2))
                {
                    g.FillRectangle(paper, 5, 3, 22, 26); g.DrawRectangle(ink, 5, 3, 22, 26);
                    for (int y = 10; y <= 22; y += 6) g.DrawLine(ink, 10, y, 22, y);
                }
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
                finally { Native.DestroyIcon(handle); }
            }
        }
        private void DragNote(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture(); Native.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width < 1 || Height < 1 || UiScale <= 0) return;
            int diameter = Px(24);
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(0, 0, diameter, diameter, 180, 90);
                path.AddArc(Width - diameter, 0, diameter, diameter, 270, 90);
                path.AddArc(Width - diameter, Height - diameter, diameter, diameter, 0, 90);
                path.AddArc(0, Height - diameter, diameter, diameter, 90, 90); path.CloseFigure();
                Region old = Region; Region = new Region(path); if (old != null) old.Dispose();
            }
        }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle = (cp.ExStyle | Native.NoActivate | 0x80) & ~0x40000; return cp; }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1; try { Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch (DllNotFoundException) { }
            if (!testing) hotkey = Native.RegisterHotKey(Handle, 1, 0x4003, (uint)Keys.H);
            bool readingKey = testing || Native.RegisterHotKey(Handle, 2, 0x4003, (uint)Keys.R);
            if (!readingKey) tooltips.SetToolTip(hint, "Ctrl + Alt + R 已被占用；请双击正文切换控制区，纯阅读时按住 Alt 拖动正文移动便签。");
            if (!testing && !hotkey) tooltips.SetToolTip(hideButton, "隐藏便签（快捷键被占用，单击右下角托盘图标可恢复）");
            EnsureProtection();
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            Native.UnregisterHotKey(Handle, 1); Native.UnregisterHotKey(Handle, 2); hotkey = false; base.OnHandleDestroyed(e);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84 && WindowState == FormWindowState.Normal)
            {
                long packed = m.LParam.ToInt64();
                Point p = PointToClient(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
                int edge = Px(7); bool left = p.X < edge, right = p.X >= ClientSize.Width - edge;
                bool top = p.Y < edge, bottom = p.Y >= ClientSize.Height - edge;
                int hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 0;
                if (hit != 0) { m.Result = new IntPtr(hit); return; }
            }
            if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE: leave PPT keyboard focus intact.
            if (m.Msg == 0x312 && m.WParam.ToInt32() == 1) { ToggleVisibility(); return; }
            if (m.Msg == 0x312 && m.WParam.ToInt32() == 2) { SetReadingMode(!prefs.ReadingOnly); return; }
            base.WndProc(ref m);
        }
        internal bool EnsureProtection()
        {
            uint actual; int error = 0;
            bool supported = Native.SupportsExclusion();
            bool ok = supported && Native.GetWindowDisplayAffinity(Handle, out actual) && actual == Native.Exclude;
            if (!ok && supported)
            {
                ok = Native.SetWindowDisplayAffinity(Handle, Native.Exclude);
                if (!ok) error = Marshal.GetLastWin32Error();
                ok = ok && Native.GetWindowDisplayAffinity(Handle, out actual) && actual == Native.Exclude;
            }
            Protected = ok; Reader.Visible = ok;
            if (ReadingSurface != null) ReadingSurface.Visible = ok;
            protection.Text = ok ? "● 捕获排除已启用" : supported ? "● 保护失败 · 讲稿已遮住" : "● 系统不支持 · 讲稿已遮住";
            tooltips.SetToolTip(protection, ok ? "Windows 捕获排除已启用；投屏前仍需验证观众端。" : supported ? "保护失败，错误码：" + error : "需要 Windows 10 2004 或更新版本。");
            protection.ForeColor = ok ? (prefs.Dark ? Color.FromArgb(162, 220, 157) : Color.FromArgb(92, 120, 63)) : (prefs.Dark ? Color.FromArgb(255, 170, 135) : Color.FromArgb(175, 67, 33));
            if (!ok && prefs.ReadingOnly && ready) SetReadingMode(false);
            return ok;
        }
        internal void ApplyTransparency(int alpha)
        {
            prefs.Alpha = Math.Max(20, Math.Min(100, alpha));
            if (TransparencySlider.Value != 100 - prefs.Alpha) TransparencySlider.Value = 100 - prefs.Alpha;
            // Hide the text during layered-window transitions, then revalidate capture exclusion.
            Reader.Visible = false; Opacity = prefs.Alpha / 100.0;
            alphaLabel.Text = (100 - prefs.Alpha) + "%";
            if (IsHandleCreated) EnsureProtection();
        }
        private Button MakeButton(NoteIcon icon, string description, EventHandler click)
        {
            Button button = new IconButton { Icon = icon, AccessibleName = description, AccessibleRole = AccessibleRole.PushButton, Width = Px(24), Height = Px(24), FlatStyle = FlatStyle.Flat, BackColor = BackColor, ForeColor = ForeColor, Margin = new Padding(0, 0, Px(4), 0), TabStop = false };
            tooltips.SetToolTip(button, description);
            button.FlatAppearance.BorderSize = 0; button.FlatAppearance.MouseOverBackColor = Color.FromArgb(238, 216, 161); button.Click += click; return button;
        }
        private void ChooseFile()
        {
            modal = true;
            try
            {
                using (OpenFileDialog dialog = new OpenFileDialog { Title = "打开讲稿（建议在共享屏幕前选择）", InitialDirectory = AppDomain.CurrentDomain.BaseDirectory, Filter = "讲稿 (*.md;*.txt;*.markdown)|*.md;*.txt;*.markdown", CheckFileExists = true, Multiselect = false })
                    if (dialog.ShowDialog(this) == DialogResult.OK) LoadFile(dialog.FileName);
            }
            finally { modal = false; Native.RaiseWithoutFocus(Handle); }
        }
        internal bool LoadFile(string path)
        {
            try
            {
                string next = ScriptFile.Read(path);
                bool nextMarkdown = !Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);
                string rtf = Markdown.Render(next, nextMarkdown, prefs.FontSize, prefs.Dark);
                Reader.Rtf = rtf; script = next; markdown = nextMarkdown;
                fileName.Text = Path.GetFileName(path) + (next.Length == 0 ? " · 空文档" : ""); ScrollToStart();
                return true;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)) throw;
                fileName.Text = "打开失败：" + (ex is InvalidDataException ? ex.Message : "请检查文件格式、路径和访问权限。");
                return false;
            }
        }
        internal void ChangeFont(int delta)
        {
            prefs.FontSize = Math.Max(6, Math.Min(42, prefs.FontSize + delta));
            sizeLabel.Text = prefs.FontSize.ToString(); Render(true);
        }
        private void Render(bool preservePosition)
        {
            if (!ready) return;
            int character = preservePosition ? Reader.GetCharIndexFromPosition(new Point(1, 1)) : 0;
            Reader.Rtf = Markdown.Render(script, markdown, prefs.FontSize, prefs.Dark);
            RestoreReadingPosition(character);
        }
        private void RestoreReadingPosition(int character)
        {
            Reader.Select(Math.Min(character, Reader.TextLength), 0); Reader.ScrollToCaret();
            int first = Native.SendMessage(Reader.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32();
            int target = Reader.GetLineFromCharIndex(Math.Min(character, Reader.TextLength));
            Native.SendMessage(Reader.Handle, 0xB6, IntPtr.Zero, new IntPtr(target - first));
        }
        internal void SetReadingMode(bool reading)
        {
            int character = ready ? Reader.GetCharIndexFromPosition(new Point(1, 1)) : 0;
            bool wasReading = prefs.ReadingOnly;
            if (wasReading && !reading) readingSize = Size;
            prefs.ReadingOnly = reading;
            MinimumSize = ModeMinimum(reading);
            if (!wasReading && reading && !readingSize.IsEmpty) Size = readingSize;
            Rectangle work = Screen.FromRectangle(Bounds).WorkingArea;
            Location = new Point(Math.Max(work.Left, Math.Min(Left, work.Right - Width)), Math.Max(work.Top, Math.Min(Top, work.Bottom - Height)));
            layout.SuspendLayout();
            for (int row = 0; row < rowHeights.Length; row++)
            {
                if (row == 3) continue;
                layout.RowStyles[row].Height = reading ? 0 : Px(rowHeights[row]);
                layout.GetControlFromPosition(0, row).Visible = !reading;
            }
            layout.Padding = reading ? new Padding(Px(3)) : new Padding(Px(10), Px(6), Px(10), Px(6));
            ReadingSurface.Margin = reading ? Padding.Empty : new Padding(Px(2), Px(4), Px(2), Px(4));
            ReadingSurface.SetReadingOnly(reading);
            layout.ResumeLayout(true);
            if (ready) RestoreReadingPosition(character);
        }
        private Size ModeMinimum(bool reading) { return reading ? new Size(Px(160), Px(90)) : new Size(Px(260), Px(240)); }
        internal void SetDarkMode(bool dark)
        {
            prefs.Dark = dark;
            ApplyColors(this, dark ? Color.FromArgb(8, 8, 8) : Color.FromArgb(255, 246, 217), dark ? Color.White : Color.FromArgb(66, 57, 40));
            fileName.ForeColor = hint.ForeColor = dark ? Color.FromArgb(200, 200, 200) : Color.FromArgb(135, 113, 77);
            title.ForeColor = dark ? Color.White : Color.FromArgb(158, 89, 38);
            ((IconButton)themeButton).Icon = dark ? NoteIcon.Sun : NoteIcon.Moon;
            themeButton.AccessibleName = dark ? "浅色模式" : "暗黑模式";
            tooltips.SetToolTip(themeButton, themeButton.AccessibleName); themeButton.Invalidate();
            ReadingSurface.Bar.Dark = dark; ReadingSurface.Bar.RefreshMetrics();
            Render(true);
            if (IsHandleCreated) EnsureProtection();
        }
        private void ApplyColors(Control control, Color background, Color foreground)
        {
            control.BackColor = background; control.ForeColor = foreground;
            Button button = control as Button;
            if (button != null)
            {
                button.BackColor = background;
                button.FlatAppearance.MouseOverBackColor = prefs.Dark ? Color.FromArgb(65, 65, 65) : Color.FromArgb(238, 216, 161);
            }
            foreach (Control child in control.Controls) ApplyColors(child, background, foreground);
        }
        internal void ScrollToStart() { Reader.Select(0, 0); Reader.ScrollToCaret(); }
        private void ToggleVisibility()
        {
            if (Visible && WindowState != FormWindowState.Minimized) HideToTray();
            else ShowFromTray();
        }
        internal void HideToTray() { Hide(); }
        internal void ShowFromTray()
        {
            if (IsDisposed || Disposing) return;
            WindowState = FormWindowState.Normal;
            EnsureProtection(); Show(); Native.RaiseWithoutFocus(Handle);
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (ready && WindowState == FormWindowState.Minimized) HideToTray();
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            keeper.Stop(); keeper.Dispose(); tooltips.Dispose();
            TrayIcon.Visible = false; TrayIcon.Dispose(); TrayMenu.Dispose(); trayArtwork.Dispose();
            if (!testing)
            {
                Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                prefs.Width = (int)Math.Round(bounds.Width / UiScale); prefs.Height = (int)Math.Round(bounds.Height / UiScale); prefs.Left = bounds.Left; prefs.Top = bounds.Top;
                if (prefs.ReadingOnly) readingSize = bounds.Size;
                if (!readingSize.IsEmpty) { prefs.ReadingWidth = (int)Math.Round(readingSize.Width / UiScale); prefs.ReadingHeight = (int)Math.Round(readingSize.Height / UiScale); }
                try { prefs.Save(settingsPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            base.OnFormClosed(e);
        }
    }
}
