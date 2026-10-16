using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpeechNote
{
    internal static class SelfTest
    {
        private static int checks;
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            checks++;
        }
        internal static int Run(string reportPath)
        {
            string output = Path.GetFullPath(reportPath);
            string folder = Path.GetDirectoryName(output);
            Directory.CreateDirectory(folder);
            try
            {
                string chinese = "中文讲稿与 emoji 🙂";
                Check(ScriptFile.Decode(Encoding.UTF8.GetBytes(chinese)) == chinese, "UTF-8 Chinese and emoji");
                Check(ScriptFile.Decode(WithPreamble(Encoding.UTF8, chinese)) == chinese, "UTF-8 BOM");
                Check(ScriptFile.Decode(WithPreamble(Encoding.Unicode, chinese)) == chinese, "UTF-16 LE");
                Check(ScriptFile.Decode(WithPreamble(Encoding.BigEndianUnicode, chinese)) == chinese, "UTF-16 BE");
                Check(ScriptFile.Decode(WithPreamble(Encoding.UTF32, chinese)) == chinese, "UTF-32 LE");
                Check(ScriptFile.Decode(WithPreamble(new UTF32Encoding(true, true), chinese)) == chinese, "UTF-32 BE");
                Check(ScriptFile.Decode(Encoding.GetEncoding(54936).GetBytes("中文讲稿")) == "中文讲稿", "GB18030");
                Check(Markdown.Escape(@"{\b malicious}") == @"\{\\b malicious\}", "RTF injection escaping");
                using (RichTextBox box = new RichTextBox())
                {
                    box.Rtf = Markdown.Render("# 标题\n**重点** 与 *强调*\n- 项目\n[链接](https://example.com)\n![配图](file:///private)\n```\n# 原样 {代码}\\\n```\n" + chinese, true, 22);
                    Check(box.Text.Contains("标题") && !box.Text.Contains("# 标题"), "Markdown headings");
                    Check(box.Text.Contains("重点") && !box.Text.Contains("**"), "Markdown emphasis");
                    Check(box.Text.Contains("•  项目"), "Markdown list");
                    Check(box.Text.Contains("# 原样 {代码}\\"), "Code fences stay literal");
                    Check(box.Text.Contains("[图片] 配图") && !box.Text.Contains("https://"), "Links/images do not load external resources");
                    Check(box.Text.Contains(chinese), "Unicode RTF round trip");
                    box.Rtf = Markdown.Render("# 原样\n**原样**\n{\\test}", false, 22);
                    Check(box.Text.Contains("# 原样") && box.Text.Contains("**原样**") && box.Text.Contains("{\\test}"), "TXT stays literal");
                }
                string fixture = Path.Combine(folder, "fixture.md");
                StringBuilder script = new StringBuilder("# 开场\n\n**重点**：我们今天要讲的内容。\n\n");
                for (int i = 1; i <= 30; i++) script.Append("## ").Append(i).Append(" · 思考与行动\n\n放慢语速，看看听众。让每一个重点被听见。\n\n");
                File.WriteAllText(fixture, script.ToString(), Encoding.UTF8);
                Check(ScriptFile.Read(fixture).Contains("开场"), "Read a real Markdown file");
                bool rejected = false;
                try { ScriptFile.Read(Path.Combine(folder, "wrong.exe")); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Reject unsupported extensions");
                string oversized = Path.Combine(folder, "oversized.txt");
                using (FileStream file = File.Create(oversized)) file.SetLength(ScriptFile.MaxBytes + 1L);
                rejected = false;
                try { ScriptFile.Read(oversized); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Reject oversized file before reading");
                string settings = Path.Combine(folder, "test-settings.ini");
                File.WriteAllText(settings, "Width=-1\nHeight=99999\nFontSize=999\nAlpha=0\nLeft=30\nTop=40");
                Preferences p = Preferences.Read(settings);
                Check(p.Width == 160 && p.Height == 2000 && p.FontSize == 42 && p.Alpha == 20, "Settings clamp invalid values");
                p.Save(settings); Preferences reread = Preferences.Read(settings);
                Check(reread.Top == 40 && reread.Left == 30 && reread.Alpha == 20, "Settings round trip");
                p.FontSize = 6; p.Dark = true; p.ReadingOnly = true; p.Width = 160; p.Height = 90; p.ReadingWidth = 160; p.ReadingHeight = 90; p.Save(settings);
                reread = Preferences.Read(settings);
                Check(reread.FontSize == 6 && reread.Dark && reread.ReadingOnly, "Small font and display modes persist");
                Check(reread.Width == 160 && reread.Height == 90 && reread.ReadingWidth == 160 && reread.ReadingHeight == 90, "Compact dimensions survive settings reload");
                using (PromptWindow window = new PromptWindow(null, true))
                {
                    IntPtr foreground = Native.GetForegroundWindow();
                    window.Show(); Application.DoEvents();
                    Check(Native.GetForegroundWindow() == foreground, "Opening does not steal focus");
                    Check(window.Protected && window.Reader.Visible, "Capture exclusion applied before showing script");
                    Check(window.Reader.Cursor == Cursors.Arrow && !window.Reader.TabStop, "Reader uses an arrow and stays out of keyboard tab order");
                    Check(Native.SendMessage(window.Reader.Handle, 0x20, window.Reader.Handle, new IntPtr(1)).ToInt32() == 1, "Native cursor override prevents RichEdit I-beam");
                    window.Reader.Select(0, 0);
                    Native.SendMessage(window.Reader.Handle, 0x201, new IntPtr(1), new IntPtr((15 << 16) | 15));
                    Native.SendMessage(window.Reader.Handle, 0x200, new IntPtr(1), new IntPtr((60 << 16) | 120));
                    Native.SendMessage(window.Reader.Handle, 0x202, IntPtr.Zero, new IntPtr((60 << 16) | 120));
                    Check(window.Reader.SelectionLength == 0 && !window.Reader.Focused, "Mouse clicks and drags do not select text or focus the reader");
                    bool noteGestureDelivered = false;
                    MouseEventHandler noteGesture = delegate { noteGestureDelivered = true; };
                    window.Reader.MouseDown += noteGesture;
                    Native.SendMessage(window.Reader.Handle, 0x201, new IntPtr(1), new IntPtr((15 << 16) | 15));
                    Native.SendMessage(window.Reader.Handle, 0x202, IntPtr.Zero, new IntPtr((15 << 16) | 15));
                    window.Reader.MouseDown -= noteGesture;
                    Check(noteGestureDelivered, "Mouse-down still reaches the note drag gesture handler");
                    Check((Native.GetWindowLong(window.Handle, Native.ExStyle) & Native.NoActivate) != 0, "No-activate window style");
                    int extendedStyle = Native.GetWindowLong(window.Handle, Native.ExStyle);
                    Check(!window.ShowInTaskbar && (extendedStyle & 0x80) != 0 && (extendedStyle & 0x40000) == 0, "Note is a tool window without a taskbar app button");
                    Check(window.TrayIcon.Icon != null && window.TrayMenu.Items.Count == 5, "Tray icon has recovery and exit actions");
                    Check((Native.GetWindowLong(window.Handle, Native.ExStyle) & 8) != 0, "Topmost window style");
                    Check(window.FormBorderStyle == FormBorderStyle.None && window.Region != null, "Borderless rounded sticky note");
                    Point edge = window.PointToScreen(new Point(window.Width - window.Px(5), window.Height - window.Px(5)));
                    IntPtr packed = new IntPtr((edge.X & 0xffff) | ((edge.Y & 0xffff) << 16));
                    Check(Native.SendMessage(window.Handle, 0x84, IntPtr.Zero, packed).ToInt32() == 17, "Bottom right resize hit area");
                    Native.SendMessage(window.Handle, 0x312, new IntPtr(1), IntPtr.Zero);
                    Check(!window.Visible, "Hide shortcut handler");
                    Native.SendMessage(window.Handle, 0x312, new IntPtr(1), IntPtr.Zero);
                    Check(window.Visible && window.Protected, "Restore shortcut keeps capture exclusion");
                    string beforeTray = window.Reader.Text;
                    window.TrayMenu.Items[0].PerformClick();
                    Check(!window.Visible, "Tray menu hides the note");
                    window.TrayMenu.Items[0].PerformClick(); Application.DoEvents();
                    Check(window.Visible && window.Protected && window.Reader.Text == beforeTray && !window.ShowInTaskbar, "Tray menu restores the same protected note without a taskbar button");
                    window.WindowState = FormWindowState.Minimized; Application.DoEvents();
                    Check(!window.Visible, "Minimizing sends the note to the tray");
                    window.ShowFromTray(); Application.DoEvents();
                    Check(window.Visible && window.WindowState == FormWindowState.Normal && window.Reader.Text == beforeTray, "Tray click restores a minimized note");
                    DoubleClickReader(window);
                    Check(window.ReadingOnly && !window.TransparencySlider.Visible && window.Reader.SelectionLength == 0, "Double-click hides controls without selecting words");
                    DoubleClickReader(window);
                    Check(!window.ReadingOnly && window.TransparencySlider.Visible, "The next double-click restores all controls");
                    window.TrayMenu.Items[1].PerformClick();
                    Check(window.ReadingOnly, "Tray menu can toggle control visibility");
                    window.TrayMenu.Items[1].PerformClick();
                    Native.SetWindowPos(window.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x13);
                    Native.RaiseWithoutFocus(window.Handle);
                    Check((Native.GetWindowLong(window.Handle, Native.ExStyle) & 8) != 0, "Restore topmost without activation");
                    foreach (int alpha in new[] { 100, 80, 50, 20, 100 })
                    {
                        window.ApplyTransparency(alpha); Application.DoEvents(); uint affinity;
                        Check(Math.Abs(window.Opacity - alpha / 100.0) < 0.01, "Opacity " + alpha);
                        Check(Native.GetWindowDisplayAffinity(window.Handle, out affinity) && affinity == Native.Exclude && window.Reader.Visible, "Capture exclusion at opacity " + alpha);
                    }
                    Native.SetWindowDisplayAffinity(window.Handle, 0);
                    Check(window.EnsureProtection(), "Repair lost capture exclusion");
                    Check(window.LoadFile(fixture) && window.Reader.Text.Contains("开场"), "Open Markdown in reader");
                    Native.SendMessage(window.Reader.Handle, 0x20A, new IntPtr(-120 << 16), IntPtr.Zero);
                    Check(Native.SendMessage(window.Reader.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() > 0, "Mouse wheel still scrolls the non-selectable reader");
                    window.ScrollToStart();
                    window.ReadingSurface.Bar.RefreshMetrics();
                    Check(window.ReadingSurface.Bar.Maximum > 0 && !window.ReadingSurface.Bar.Thumb.IsEmpty, "Custom thumb reflects overflowing Markdown");
                    Check(window.Reader.ClientSize.Width == window.Reader.Parent.ClientSize.Width, "Native scrollbar is fully clipped outside the reading viewport");
                    window.ReadingSurface.Bar.ScrollToFraction(0.5); Application.DoEvents();
                    Check(window.ReadingSurface.Bar.Position > window.ReadingSurface.Bar.Maximum / 3, "Custom scrollbar reaches the middle");
                    window.ReadingSurface.Bar.ScrollToFraction(1); Application.DoEvents();
                    Check(window.ReadingSurface.Bar.Position >= window.ReadingSurface.Bar.Maximum - window.ReadingSurface.Bar.Page, "Custom scrollbar reaches the end");
                    window.ScrollToStart(); window.ReadingSurface.Bar.RefreshMetrics();
                    Check(window.ReadingSurface.Bar.Position == 0, "Return-to-top synchronizes the custom thumb");
                    string previous = window.Reader.Text;
                    Check(!window.LoadFile(Path.Combine(folder, "missing.txt")) && window.Reader.Text == previous, "Failed load preserves previous script");
                    window.LoadFile(fixture);
                    Native.SendMessage(window.Reader.Handle, 0xB6, IntPtr.Zero, new IntPtr(15)); Application.DoEvents();
                    Check(Native.SendMessage(window.Reader.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() > 0, "Reader scrolls down");
                    window.ChangeFont(2); Application.DoEvents();
                    Check(window.FontPoints == 24 && window.Reader.Text.Contains("开场"), "Font changes preserve document");
                    window.ScrollToStart(); Application.DoEvents();
                    Check(Native.SendMessage(window.Reader.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() == 0, "Return to top");
                    window.Size = new Size(window.Px(400), window.Px(440)); Application.DoEvents();
                    Check(window.Reader.Height > 100 && window.TransparencySlider.Width > 50, "Minimum size remains usable");
                    SavePreview(window, Path.Combine(folder, "preview-small.png"));
                    window.Size = new Size(window.Px(480), window.Px(680)); window.ChangeFont(-2); Application.DoEvents();
                    SavePreview(window, Path.Combine(folder, "preview.png"));
                    window.ChangeFont(-100);
                    Check(window.FontPoints == 6, "Minimum font is six points");
                    window.ChangeFont(1);
                    Check(window.FontPoints == 7, "Seven point font is reachable");
                    window.ChangeFont(100);
                    Check(window.FontPoints == 42, "Maximum font stays bounded");
                    window.ChangeFont(-20); window.ScrollToStart();
                    Native.SendMessage(window.Reader.Handle, 0xB6, IntPtr.Zero, new IntPtr(12));
                    int anchor = window.Reader.GetCharIndexFromPosition(new Point(1, 1));
                    window.SetDarkMode(true); Application.DoEvents();
                    Check(window.Dark && window.Reader.BackColor.R < 16, "Dark mode uses a near-black paper");
                    Check(window.Reader.GetCharIndexFromPosition(new Point(1, 1)) == anchor, "Theme change preserves reading position");
                    int body = window.Reader.Text.IndexOf("放慢");
                    window.Reader.Select(body, 1);
                    Check(window.Reader.SelectionColor.ToArgb() == Color.White.ToArgb(), "Dark mode renders body text white");
                    Check(window.Protected, "Theme change retains capture protection");
                    window.ScrollToStart(); Application.DoEvents();
                    SavePreview(window, Path.Combine(folder, "preview-dark.png"));
                    int readingHeight = window.Reader.Height;
                    Native.SendMessage(window.Handle, 0x312, new IntPtr(2), IntPtr.Zero); Application.DoEvents();
                    Check(window.ReadingOnly && window.Reader.Height > readingHeight + window.Px(100), "Reading mode expands script area");
                    TableLayoutPanel mainLayout = (TableLayoutPanel)window.Controls[0];
                    bool hidden = true;
                    foreach (Control control in mainLayout.Controls) if (control != window.ReadingSurface && control.Visible) hidden = false;
                    Check(hidden && window.Reader.Visible, "Reading mode hides all settings and hints");
                    Check(!window.ReadingSurface.Bar.Visible && window.Protected, "Reading mode is uncluttered and protected");
                    SavePreview(window, Path.Combine(folder, "preview-reading.png"));
                    Native.SendMessage(window.Reader.Handle, 0xB6, IntPtr.Zero, new IntPtr(12));
                    Check(Native.SendMessage(window.Reader.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() > 0, "Reading mode still scrolls without a scrollbar");
                    anchor = window.Reader.GetCharIndexFromPosition(new Point(1, 1));
                    Native.SendMessage(window.Handle, 0x312, new IntPtr(2), IntPtr.Zero); Application.DoEvents();
                    Check(!window.ReadingOnly && window.TransparencySlider.Visible, "Reading shortcut restores controls");
                    Check(Math.Abs(window.Reader.GetCharIndexFromPosition(new Point(1, 1)) - anchor) < 40, "Restoring settings retains the reading passage");
                    window.SetDarkMode(false);
                    Check(window.Reader.BackColor.R == 255 && !window.Dark && window.Protected, "Return to light mode keeps protection");
                    window.Size = new Size(window.Px(260), window.Px(240)); window.ChangeFont(-13); window.ScrollToStart(); Application.DoEvents();
                    Check(window.Width == window.Px(260) && window.Height == window.Px(240) && window.Reader.Height >= window.Px(45), "Compact settings mode retains readable content");
                    CheckButtons(window);
                    SavePreview(window, Path.Combine(folder, "preview-compact.png"));
                    window.SetReadingMode(true);
                    window.Size = new Size(window.Px(160), window.Px(90)); Application.DoEvents();
                    Check(window.Size == new Size(window.Px(160), window.Px(90)) && window.Reader.Width >= window.Px(135) && window.Reader.Height >= window.Px(65), "Tiny reading window keeps usable text area");
                    Size tiny = window.Size;
                    SavePreview(window, Path.Combine(folder, "preview-tiny.png"));
                    Native.SendMessage(window.Reader.Handle, 0xB6, IntPtr.Zero, new IntPtr(8));
                    Check(Native.SendMessage(window.Reader.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() > 0 && window.Protected, "Tiny reader still scrolls and retains protection");
                    window.SetReadingMode(false); Application.DoEvents();
                    Check(window.Width >= window.Px(260) && window.Height >= window.Px(240) && window.TransparencySlider.Visible, "Opening settings expands a tiny note");
                    window.SetReadingMode(true); Application.DoEvents();
                    Check(window.Size == tiny, "Closing settings restores the tiny note size");
                    window.SetDarkMode(true); window.ScrollToStart(); Application.DoEvents();
                    SavePreview(window, Path.Combine(folder, "preview-tiny-dark.png"));
                    window.SetReadingMode(false); window.Size = new Size(window.Px(480), window.Px(680)); window.ChangeFont(13);
                    string longScript = Path.Combine(folder, "long-scroll.txt");
                    StringBuilder longText = new StringBuilder();
                    for (int i = 0; i < 2500; i++) longText.Append("讲稿第 ").Append(i).Append(" 段\n");
                    File.WriteAllText(longScript, longText.ToString()); window.LoadFile(longScript); Application.DoEvents();
                    window.ReadingSurface.Bar.RefreshMetrics();
                    window.ReadingSurface.Bar.ScrollToFraction(1); Application.DoEvents();
                    Check(window.ReadingSurface.Bar.Position >= window.ReadingSurface.Bar.Maximum - window.ReadingSurface.Bar.Page && Native.SendMessage(window.Reader.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32() >= 2450, "Long-document thumb reaches the actual last page without wrapping");
                    window.ReadingSurface.Bar.ScrollToFraction(0.5); Application.DoEvents();
                    int middleLine = Native.SendMessage(window.Reader.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    Check(middleLine > 1100 && middleLine < 1400, "Long-document thumb maps to the actual middle passage");
                    window.ReadingSurface.Bar.ScrollToFraction(0); Application.DoEvents();
                    Check(window.ReadingSurface.Bar.Position == 0, "Long-document thumb returns to the beginning");
                    string shortScript = Path.Combine(folder, "short-scroll.txt"); File.WriteAllText(shortScript, "一行讲稿");
                    window.LoadFile(shortScript); Application.DoEvents(); window.ReadingSurface.Bar.RefreshMetrics();
                    Check(window.ReadingSurface.Bar.Thumb.IsEmpty, "No scrollbar thumb when all text fits; max=" + window.ReadingSurface.Bar.Maximum + ", page=" + window.ReadingSurface.Bar.Page + ", pos=" + window.ReadingSurface.Bar.Position);
                    Check(Native.GetForegroundWindow() == foreground, "Opacity, resize and topmost do not steal focus");
                    window.HideToTray(); window.TrayMenu.Items[4].PerformClick();
                    Check(window.IsDisposed && window.TrayMenu.IsDisposed && !window.TrayIcon.Visible, "Exit from tray disposes the window, menu and icon");
                }
                File.WriteAllText(output, "PASS: " + checks + " checks.\r\nNative capture affinity verified across 20%-100% opacity.\r\nPPT fullscreen and DingTalk/Feishu audience-side capture require manual validation.\r\nPreviews are internal renderings, not desktop capture tests.\r\n", Encoding.UTF8);
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(output, ex.ToString(), Encoding.UTF8); return 1; }
        }
        private static byte[] WithPreamble(Encoding encoding, string text)
        {
            byte[] preamble = encoding.GetPreamble(), data = encoding.GetBytes(text);
            byte[] result = new byte[preamble.Length + data.Length];
            Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length); Buffer.BlockCopy(data, 0, result, preamble.Length, data.Length);
            return result;
        }
        private static void DoubleClickReader(PromptWindow window)
        {
            Native.SendMessage(window.Reader.Handle, 0x203, new IntPtr(1), new IntPtr((12 << 16) | 12));
            Native.SendMessage(window.Reader.Handle, 0x202, IntPtr.Zero, new IntPtr((12 << 16) | 12));
            Application.DoEvents();
        }
        private static void CheckButtons(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                Button button = child as Button;
                if (button != null)
                    Check(button is IconButton && button.Text == "" && !string.IsNullOrEmpty(button.AccessibleName) && button.Right <= parent.ClientSize.Width && button.Bottom <= parent.ClientSize.Height, "Icon has an accessible name and fits the compact toolbar");
                CheckButtons(child);
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PrintRange
        {
            public IntPtr Hdc, HdcTarget;
            public Rect Area, Page;
            public int Start, End;
        }
        private static void SavePreview(PromptWindow window, string path)
        {
            using (Bitmap image = new Bitmap(window.Width, window.Height))
            {
                image.SetResolution(96 * window.UiScale, 96 * window.UiScale);
                window.DrawToBitmap(image, new Rectangle(Point.Empty, window.Size));
                // RichTextBox does not support DrawToBitmap; render its own RTF through EM_FORMATRANGE.
                Point point = window.PointToClient(window.Reader.PointToScreen(Point.Empty));
                Rectangle content = new Rectangle(point.X, point.Y, window.Reader.ClientSize.Width, window.Reader.ClientSize.Height);
                using (Graphics g = Graphics.FromImage(image))
                {
                    using (SolidBrush paper = new SolidBrush(window.BackColor))
                    {
                        g.FillRectangle(paper, content);
                        // DrawToBitmap does not honor ancestor clipping for the oversized RichEdit child.
                        // Remove the off-viewport native gutter just as the live parent window clips it.
                        g.FillRectangle(paper, new Rectangle(content.Right, point.Y, window.Reader.Width - window.Reader.ClientSize.Width, window.Reader.Height));
                    }
                    IntPtr hdc = g.GetHdc(); IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PrintRange)));
                    try
                    {
                        double twips = 1440.0 / (96 * window.UiScale);
                        PrintRange range = new PrintRange { Hdc = hdc, HdcTarget = hdc,
                            Area = new Rect { Left = (int)(content.Left * twips), Top = (int)(content.Top * twips), Right = (int)(content.Right * twips), Bottom = (int)(content.Bottom * twips) },
                            Page = new Rect { Right = (int)(image.Width * twips), Bottom = (int)(image.Height * twips) }, Start = 0, End = -1 };
                        Marshal.StructureToPtr(range, memory, false);
                        Native.SendMessage(window.Reader.Handle, 0x439, new IntPtr(1), memory);
                        Native.SendMessage(window.Reader.Handle, 0x439, IntPtr.Zero, IntPtr.Zero);
                    }
                    finally { Marshal.FreeHGlobal(memory); g.ReleaseHdc(hdc); }
                }
                if (window.ReadingSurface.Bar.Visible)
                {
                    Point barPoint = window.PointToClient(window.ReadingSurface.Bar.PointToScreen(Point.Empty));
                    window.ReadingSurface.Bar.DrawToBitmap(image, new Rectangle(barPoint, window.ReadingSurface.Bar.Size));
                }
                image.Save(path, ImageFormat.Png);
            }
        }
    }
}
