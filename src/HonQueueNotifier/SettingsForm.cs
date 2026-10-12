using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HonQueueNotifier
{
    // =====================================================================================
    // Settings dialog. Standard Windows controls. The rules (defaults, validation, saving)
    // live in Settings; this form only shows them and binds the controls.
    // =====================================================================================
    sealed class SettingsForm : Form
    {
        static readonly string[] PositionNames = { "Top center", "Top left", "Top right", "Bottom center", "Bottom left", "Bottom right", "Center" };
        static readonly string[] ScreenNames = { "Main display only", "Every display", "The display with the mouse pointer" };

        readonly Func<Settings, string> _apply;
        readonly Action _testAlert;
        Settings _base;

        readonly Dictionary<string, Control> _fields = new Dictionary<string, Control>();
        readonly Dictionary<string, Label> _problems = new Dictionary<string, Label>();
        readonly TabControl _tabs = new SheetTabs();
        readonly Label _status = new Label();
        readonly Button _applyButton = new Button();
        string _shownIni;
        bool _shownAutostart;
        readonly Dictionary<NumericUpDown, string> _shownNumbers = new Dictionary<NumericUpDown, string>();

        CheckBox _autostart, _popup, _flash, _sound, _phone, _minimize;
        RadioButton _logAuto, _logFile, _soundBuiltIn, _soundFile, _smart, _always;
        TextBox _log, _soundPath, _server, _topic, _hotkey;
        Button _browseLog, _browseSound, _play, _testAlertButton, _testPush;
        ComboBox _position, _screens, _priority;
        NumericUpDown _repeat, _repeatFor, _after, _idle;
        Label _pushResult;
        // The parts of the dialog that belong to one option: greyed out, texts and all, while it is off.
        Control[] _hotkeyParts, _logFileParts, _popupParts, _soundParts, _soundFileParts, _phoneParts, _smartParts;

        readonly string _settingsPath, _logPath;
        readonly Font _headingFont;

        public SettingsForm(Settings current, Icon icon, Func<Settings, string> apply, Action testAlert)
            : this(current, icon, apply, testAlert, null, null)
        {
        }

        public SettingsForm(Settings current, Icon icon, Func<Settings, string> apply, Action testAlert, string settingsPath, string logPath)
        {
            _settingsPath = settingsPath;
            _logPath = logPath;
            _base = current.Clone();
            _apply = apply;
            _testAlert = testAlert;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            _headingFont = HeadingFont(Font);
            Text = Settings.DisplayName + " settings";
            if (icon != null) Icon = icon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(600, 580);
            Padding = new Padding(10);   // the tabs sit inside the dialog like a sheet, not against its edges

            _tabs.Dock = DockStyle.Fill;
            _tabs.TabPages.Add(GeneralTab());
            _tabs.TabPages.Add(PopupTab());
            _tabs.TabPages.Add(SoundTab());
            _tabs.TabPages.Add(PhoneTab());

            Button ok = new Button();
            ok.Text = "OK";
            ok.Click += delegate { if (TryApply()) Close(); };
            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Click += delegate { Close(); };
            _applyButton.Text = "Apply";
            _applyButton.Click += delegate { TryApply(); };
            _applyButton.Margin = new Padding(3, 3, 0, 3);   // its right edge in line with the tabs
            AcceptButton = ok;
            CancelButton = cancel;

            _status.AutoSize = true;
            _status.ForeColor = Color.Firebrick;
            _status.Anchor = AnchorStyles.Left;

            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Bottom;
            bottom.AutoSize = true;
            bottom.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bottom.Padding = new Padding(0, 10, 0, 0);
            bottom.ColumnCount = 4;
            bottom.RowCount = 1;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 3; i++) bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(_status, 0, 0);
            bottom.Controls.Add(ok, 1, 0);
            bottom.Controls.Add(cancel, 2, 0);
            bottom.Controls.Add(_applyButton, 3, 0);

            Controls.Add(_tabs);
            Controls.Add(bottom);
            WatchChanges(_tabs);
            LoadFrom(_base);
            // Only now: joining the form gives every control the dialog's font, and with the tables already
            // laid out, each label would lay its table out again.
            foreach (TabPage page in _tabs.TabPages) page.Controls[0].ResumeLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        // ---- layout helpers -------------------------------------------------------------

        // Spacing, in pixels at 96 DPI (the form scales it with the display). A note sits right under the
        // option it explains, in line with the option's text; options stand further apart, and sections
        // further still, so it is plain what belongs together. Headings, labels, check boxes and radio
        // buttons all start on the same line (DialogLabel draws its text right at its left edge).
        internal const int TextIndent = 18;     // where the text of a check box or radio button starts
        const int OptionGap = 10, SectionGap = 20;
        const int NoteWidth = 540;

        static TabPage Page(string title, out TableLayoutPanel table)
        {
            // White, as Windows 10 drew a tab page. Windows 11 draws it in nearly the same grey as the
            // dialog around it, so the page did not stand out as a page.
            TabPage page = new TabPage(title);
            page.UseVisualStyleBackColor = false;
            page.BackColor = SystemColors.Window;
            page.AutoScroll = true;
            table = new TableLayoutPanel();
            // Laid out once when the dialog is built, not after every control added to it: an auto-sized
            // table measures all its text again each time, which made the dialog take 1.5 s to open.
            table.SuspendLayout();
            table.Dock = DockStyle.Top;
            table.AutoSize = true;
            table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            table.Padding = new Padding(10);
            table.ColumnCount = 3;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            page.Controls.Add(table);
            return page;
        }

        static int NextRow(TableLayoutPanel t)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowCount = t.RowStyles.Count;
            return t.RowCount - 1;
        }

        // Moves a control right by indent, so that it lines up under the text of the option above it.
        static T Indented<T>(T c, int indent) where T : Control
        {
            if (indent != 0) c.Margin = new Padding(c.Margin.Left + indent, c.Margin.Top, c.Margin.Right, c.Margin.Bottom);
            return c;
        }

        // A control across all three columns.
        static void Wide(TableLayoutPanel t, Control c, int indent = 0)
        {
            int row = NextRow(t);
            t.Controls.Add(Indented(c, indent), 0, row);
            t.SetColumnSpan(c, 3);
        }

        // Label | field | optional extra (a button, or a row of them).
        static void Field(TableLayoutPanel t, string label, Control field, Control extra, int indent = 0)
        {
            int row = NextRow(t);
            if (label != null) t.Controls.Add(Indented(Caption(label), indent), 0, row);
            field.Anchor = field is TextBox ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left;
            t.Controls.Add(field, 1, row);
            if (extra != null) { extra.Anchor = AnchorStyles.Left; t.Controls.Add(extra, 2, row); }
        }

        // Empty room between options, or before a section.
        static void Space(TableLayoutPanel t, int height)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.Size = new Size(1, height);
            l.Margin = Padding.Empty;
            Wide(t, l);
        }

        static Label Caption(string text)
        {
            Label l = new DialogLabel();
            l.Text = text;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            return l;
        }

        // One short grey line right under the option it explains, in line with that option's text.
        static Label Note(TableLayoutPanel t, string text, int indent)
        {
            Label l = Caption(text);
            l.ForeColor = SystemColors.GrayText;
            l.Margin = new Padding(3, 0, 3, 3);
            l.MaximumSize = new Size(NoteWidth - indent, 0);
            Wide(t, l, indent);
            return l;
        }

        // The same under a text field, in line with the field.
        static Label FieldNote(TableLayoutPanel t, string text)
        {
            Label l = Caption(text);
            l.ForeColor = SystemColors.GrayText;
            l.Margin = new Padding(3, 0, 3, 3);
            l.MaximumSize = new Size(NoteWidth - 120, 0);
            int row = NextRow(t);
            t.Controls.Add(l, 1, row);   // not into the buttons' column: that would widen it and narrow the field
            return l;
        }

        // A section: room above it, then its title, a step larger than the text and semibold.
        void Heading(TableLayoutPanel t, string text)
        {
            Space(t, SectionGap);
            Label l = Caption(text);
            l.Font = _headingFont;
            l.Margin = new Padding(3, 0, 3, 3);
            Wide(t, l);
        }

        // The controls added to a page since index start: the part of the page that belongs to one option.
        // Red problem lines are left out; they must stay readable.
        Control[] PartsSince(TableLayoutPanel t, int start)
        {
            List<Control> parts = new List<Control>();
            for (int i = start; i < t.Controls.Count; i++)
            {
                Label l = t.Controls[i] as Label;
                if (l == null || !_problems.ContainsValue(l)) parts.Add(t.Controls[i]);
            }
            return parts.ToArray();
        }

        // Windows 11 draws the title bar of a classic dialog a shade lighter than the dialog itself, which
        // shows as a seam. The title bar gets the dialog's own colour instead, unless the accent colour is
        // wanted on title bars. Earlier Windows have no such setting and keep their own title bar.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            CaptionColored = Native.IsWindows && !AccentTitleBars() && Native.SetCaptionColor(Handle, BackColor);
        }

        public bool CaptionColored { get; private set; }

        public static bool AccentTitleBars()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM", false))
                    return k != null && k.GetValue("ColorPrevalence") is int && (int)k.GetValue("ColorPrevalence") == 1;
            }
            catch (Exception) { return false; }
        }

        // Segoe UI Semibold where the dialog's font is Segoe UI (Windows 10 and 11 in most languages),
        // otherwise the dialog's own font in bold.
        static Font HeadingFont(Font body)
        {
            float size = body.SizeInPoints * 7f / 6f;
            Font semibold = new Font("Segoe UI Semibold", size);
            if (body.Name == "Segoe UI" && semibold.Name == "Segoe UI Semibold") return semibold;
            semibold.Dispose();
            return new Font(body.FontFamily, size, FontStyle.Bold);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _headingFont != null) _headingFont.Dispose();
            base.Dispose(disposing);
        }

        static CheckBox Check(string text)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Anchor = AnchorStyles.Left;
            return c;
        }

        // Radio buttons only exclude each other inside one panel, and a layout change can move one of a
        // group into a panel of its own. This links a group explicitly.
        static void OneOf(params RadioButton[] group)
        {
            foreach (RadioButton r in group)
            {
                RadioButton me = r;
                me.CheckedChanged += delegate
                {
                    if (!me.Checked) return;
                    foreach (RadioButton other in group) if (other != me) other.Checked = false;
                };
            }
        }

        static RadioButton Radio(string text)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.AutoSize = true;
            r.Anchor = AnchorStyles.Left;
            return r;
        }

        static Button Btn(string text, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(6, 0, 6, 0);
            b.Click += click;
            return b;
        }

        static NumericUpDown Number(int min, int max)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Width = 70;
            n.Anchor = AnchorStyles.Left;
            return n;
        }

        static ComboBox Choice(string[] items)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Items.AddRange(items);
            c.Width = 240;
            c.MinimumSize = new Size(240, 0);   // without it, scaling for a high DPI collapses the box to zero width
            return c;
        }

        // Several controls on one line, vertically centred.
        static TableLayoutPanel Inline(params Control[] parts)
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.SuspendLayout();   // laid out once, as a page is
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.Margin = new Padding(0);
            t.Anchor = AnchorStyles.Left;
            t.RowCount = 1;
            t.ColumnCount = parts.Length;
            for (int i = 0; i < parts.Length; i++)
            {
                t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                parts[i].Anchor = AnchorStyles.Left;
                t.Controls.Add(parts[i], i, 0);
            }
            t.ResumeLayout();
            return t;
        }

        // Numbers that belong to one option, one per line: label, box, unit. The boxes line up in one
        // column, so no value hangs in the middle of a sentence that wraps.
        static TableLayoutPanel NumberRows(params object[] rows)
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.SuspendLayout();   // laid out once, as a page is
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.Margin = new Padding(0);
            t.Anchor = AnchorStyles.Left;
            t.ColumnCount = 3;
            for (int i = 0; i < 3; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i + 2 < rows.Length; i += 3)
            {
                int row = t.RowCount++;
                t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                Control box = (Control)rows[i + 1];
                box.Anchor = AnchorStyles.Left;
                t.Controls.Add(Caption((string)rows[i]), 0, row);
                t.Controls.Add(box, 1, row);
                t.Controls.Add(Caption((string)rows[i + 2]), 2, row);
            }
            t.ResumeLayout();
            return t;
        }

        // A red line under a field, shown only while that field has a problem: in line with the field,
        // or, for a field that sits inside a line of text, with that line's text.
        void ProblemLine(TableLayoutPanel t, string key, Control field, int indent = -1)
        {
            Label l = Caption("");
            l.ForeColor = Color.Firebrick;
            l.Visible = false;
            int row = NextRow(t);
            if (indent < 0) { t.Controls.Add(l, 1, row); t.SetColumnSpan(l, 2); }
            else { t.Controls.Add(Indented(l, indent), 0, row); t.SetColumnSpan(l, 3); }
            _problems[key] = l;
            _fields[key] = field;
        }

        // ---- tabs ------------------------------------------------------------------------

        TabPage GeneralTab()
        {
            TableLayoutPanel t;
            TabPage page = Page("General", out t);
            _autostart = Check("Start with Windows");
            Wide(t, _autostart);

            Heading(t, "Game log");
            _logAuto = Radio("Find it automatically (recommended)");
            _logAuto.CheckedChanged += delegate { UpdateEnabled(); };
            Wide(t, _logAuto);
            _logFile = Radio("Use this file:");
            _log = new TextBox();
            _browseLog = Btn("Browse...", delegate { BrowseLog(); });
            Field(t, null, _log, _browseLog);
            t.Controls.Add(_logFile, 0, t.RowCount - 1);
            OneOf(_logAuto, _logFile);
            ProblemLine(t, "General.LogPath", _log);
            _logFileParts = new Control[] { _log, _browseLog };

            Heading(t, "Minimize hotkey");
            _minimize = Check("Minimize the game with");
            _minimize.CheckedChanged += delegate { UpdateEnabled(); };
            _hotkey = new TextBox();
            _hotkey.ReadOnly = true;
            _hotkey.BackColor = SystemColors.Window;
            _hotkey.ShortcutsEnabled = false;
            _hotkey.Width = 130;
            _hotkey.KeyDown += HotkeyKeyDown;
            Wide(t, Inline(_minimize, _hotkey));
            ProblemLine(t, "General.MinimizeHotkey", _hotkey, TextIndent);
            Label hotkeyNote = Note(t, "Click the box and press new keys. Only active while the game has focus.", TextIndent);
            _hotkeyParts = new Control[] { _hotkey, hotkeyNote };

            if (_settingsPath != null)
            {
                Heading(t, "Settings and log");
                TextBox folder = new TextBox();
                folder.ReadOnly = true;
                folder.Text = Path.GetDirectoryName(Path.GetFullPath(_settingsPath));
                Field(t, "Folder:", folder, null);
                Button openFolder = Btn("Open folder", delegate { ShowInExplorer(_settingsPath); });
                Button openLog = Btn("Open log", delegate { AppFiles.Open(_logPath); });
                openLog.Enabled = _logPath != null;
                Field(t, null, Inline(openFolder, openLog), null);
            }

            Heading(t, "About");
            Field(t, "Version:", Caption(Program.Version), null);
            string repo = Program.RepoUrl.Substring(Program.RepoUrl.IndexOf("//", StringComparison.Ordinal) + 2);
            DialogLabel source = (DialogLabel)Caption(repo);
            source.SetLink(repo, Program.RepoUrl);
            Field(t, "Source code:", source, null);
            return page;
        }

        TabPage PopupTab()
        {
            TableLayoutPanel t;
            TabPage page = Page("Popup", out t);
            _popup = Check("Show a popup when a match is found and the game is not focused");
            _popup.CheckedChanged += delegate { UpdateEnabled(); };
            Wide(t, _popup);
            int start = t.Controls.Count;
            _position = Choice(PositionNames);
            Field(t, "Position:", _position, null, TextIndent);
            ProblemLine(t, "Popup.Position", _position);
            _screens = Choice(ScreenNames);
            Field(t, "Show it on:", _screens, null, TextIndent);
            ProblemLine(t, "Popup.Screens", _screens);
            _popupParts = PartsSince(t, start);
            Space(t, OptionGap);
            _flash = Check("Flash the game's taskbar button");
            Wide(t, _flash);
            Space(t, SectionGap);
            _testAlertButton = Btn("Apply and show a test alert", delegate { if (TryApply() && _testAlert != null) _testAlert(); });
            Wide(t, _testAlertButton);
            return page;
        }

        TabPage SoundTab()
        {
            TableLayoutPanel t;
            TabPage page = Page("Sound", out t);
            _sound = Check("Play a sound when a match is found");
            _sound.CheckedChanged += delegate { UpdateEnabled(); };
            Wide(t, _sound);
            int start = t.Controls.Count;
            _soundBuiltIn = Radio("Built-in sound");
            _soundBuiltIn.CheckedChanged += delegate { UpdateEnabled(); };
            Wide(t, _soundBuiltIn, TextIndent);
            _soundFile = Radio("This .wav file:");
            _soundPath = new TextBox();
            _browseSound = Btn("Browse...", delegate { BrowseSound(); });
            Field(t, null, _soundPath, _browseSound);
            t.Controls.Add(Indented(_soundFile, TextIndent), 0, t.RowCount - 1);
            OneOf(_soundBuiltIn, _soundFile);
            ProblemLine(t, "Sound.File", _soundPath);
            _play = Btn("Play", delegate { Chime.Play(_soundFile.Checked ? _soundPath.Text : ""); });
            Wide(t, _play, TextIndent);
            Space(t, OptionGap);
            _repeat = Number(0, 3600);
            _repeatFor = Number(0, 86400);
            Wide(t, NumberRows(
                "Repeat every:", _repeat, "seconds until I react (0 = play once)",
                "Stop repeating after:", _repeatFor, "seconds"), TextIndent);
            _soundParts = PartsSince(t, start);
            _soundFileParts = new Control[] { _soundPath, _browseSound };
            return page;
        }

        TabPage PhoneTab()
        {
            TableLayoutPanel t;
            TabPage page = Page("Phone", out t);
            _phone = Check("Send match alerts to my phone with ntfy (free, no account needed)");
            _phone.CheckedChanged += delegate { UpdateEnabled(); };
            Wide(t, _phone);
            int start = t.Controls.Count;
            Space(t, OptionGap);
            _topic = new TextBox();
            Field(t, "Topic:", _topic, null);
            ProblemLine(t, "Phone.NtfyTopic", _topic);
            FieldNote(t, "Pick a name only you would use. Anyone with the same topic gets your alerts.");

            Heading(t, "Set up your phone");
            DialogLabel install = (DialogLabel)Caption("1. Install the free ntfy app from the App Store or Google Play (ntfy.sh).");
            install.SetLink("ntfy.sh", "https://ntfy.sh");
            Wide(t, install);
            Wide(t, Caption("2. In the app, tap + and enter the topic above."));
            _testPush = Btn("Send a test notification", delegate { SendTestPush(); });
            _pushResult = Caption("");
            // The button starts where the words of steps 1 and 2 do: the number with its space, then the
            // button with no margin between them.
            DialogLabel three = (DialogLabel)Caption("3. ");
            three.StepNumber = true;
            three.Margin = new Padding(three.Margin.Left, three.Margin.Top, 0, three.Margin.Bottom);
            _testPush.Margin = new Padding(0, _testPush.Margin.Top, _testPush.Margin.Right, _testPush.Margin.Bottom);
            Wide(t, Inline(three, _testPush, _pushResult));

            Heading(t, "When to send");
            _smart = Radio("When I have not reacted to the popup");
            _smart.CheckedChanged += delegate { UpdateEnabled(); };
            Wide(t, _smart);
            _after = Number(0, 3600);
            _idle = Number(1, 86400);
            Wide(t, NumberRows(
                "After:", _after, "seconds",
                "Or at once when idle for:", _idle, "seconds (no keyboard or mouse input)"), TextIndent);
            _always = Radio("On every match found, even when the game is focused");
            Wide(t, _always);
            OneOf(_smart, _always);
            ProblemLine(t, "Phone.Mode", _smart);
            _smartParts = new Control[] { _after, _idle };   // its fields; its words stay, it can still be picked

            Heading(t, "Advanced");
            _priority = Choice(Settings.NtfyPriorities);
            _priority.MinimumSize = new Size(110, 0);
            _priority.Width = 110;
            Field(t, "Priority:", _priority, null);
            ProblemLine(t, "Phone.NtfyPriority", _priority);
            _server = new TextBox();
            Field(t, "Server:", _server, null);
            ProblemLine(t, "Phone.NtfyServer", _server);
            _phoneParts = PartsSince(t, start);
            return page;
        }

        // The key-capture box: whatever combination is pressed while it has focus becomes its text.
        void HotkeyKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            Keys code = e.KeyCode;
            if (code == Keys.ControlKey || code == Keys.Menu || code == Keys.ShiftKey || code == Keys.LWin || code == Keys.RWin) return;
            uint m = 0;
            if (e.Control) m |= HotkeyText.Ctrl;
            if (e.Alt) m |= HotkeyText.Alt;
            if (e.Shift) m |= HotkeyText.Shift;
            if (m == 0) { if (code == Keys.Back || code == Keys.Delete) _hotkey.Text = ""; return; }
            _hotkey.Text = HotkeyText.Format(m, code);
        }

        // ---- binding ---------------------------------------------------------------------

        static int Clamp(NumericUpDown n, int v) { return (int)Math.Max(n.Minimum, Math.Min(n.Maximum, v)); }

        public void LoadFrom(Settings s)
        {
            try { _autostart.Checked = TrayApp.IsAutostart(); } catch (Exception) { }
            bool auto = string.IsNullOrWhiteSpace(s.LogPath);
            _logAuto.Checked = auto;
            _logFile.Checked = !auto;
            _log.Text = auto ? "" : s.LogPath.Trim();
            _minimize.Checked = s.MinimizeHotkeyEnabled;
            _hotkey.Text = HotkeyText.Normalize(s.MinimizeHotkey) ?? s.MinimizeHotkey;

            _popup.Checked = s.PopupEnabled;
            _position.SelectedIndex = Math.Max(0, Settings.IndexOf(Settings.Positions, s.PopupPosition));
            _screens.SelectedIndex = Math.Max(0, Settings.IndexOf(Settings.ScreenModes, s.PopupScreens));
            _flash.Checked = s.FlashTaskbar;

            _sound.Checked = s.SoundEnabled;
            bool builtIn = string.IsNullOrWhiteSpace(s.SoundFile);
            _soundBuiltIn.Checked = builtIn;
            _soundFile.Checked = !builtIn;
            _soundPath.Text = builtIn ? "" : s.SoundFile.Trim();
            _repeat.Value = Clamp(_repeat, s.SoundRepeatSeconds);
            _repeatFor.Value = Clamp(_repeatFor, s.SoundRepeatForSeconds);

            _phone.Checked = s.PhoneEnabled;
            _server.Text = s.NtfyServer;
            _topic.Text = s.NtfyTopic;
            string pr = (s.NtfyPriority ?? "").Trim();
            int pi = Settings.IndexOf(Settings.NtfyPriorities, pr);
            if (pi < 0 && pr.Length == 1 && pr[0] >= '1' && pr[0] <= '5') pi = pr[0] - '1';
            _priority.SelectedIndex = pi < 0 ? 3 : pi;
            _always.Checked = s.PhoneAlways;
            _smart.Checked = !s.PhoneAlways;
            _after.Value = Clamp(_after, s.PhoneAfterSeconds);
            _idle.Value = Clamp(_idle, s.PhoneIdleSeconds);
            UpdateEnabled();
            MarkUnchanged();
        }

        static string OneLine(string text)
        {
            return (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        // The settings as the dialog shows them. The dialog shows every setting (a check makes sure).
        public Settings Collect()
        {
            Settings s = _base.Clone();
            s.LogPath = _logAuto.Checked ? "" : OneLine(_log.Text);
            s.MinimizeHotkeyEnabled = _minimize.Checked;
            s.MinimizeHotkey = OneLine(_hotkey.Text);
            s.PopupEnabled = _popup.Checked;
            s.PopupPosition = Settings.Positions[Math.Max(0, _position.SelectedIndex)];
            s.PopupScreens = Settings.ScreenModes[Math.Max(0, _screens.SelectedIndex)];
            s.FlashTaskbar = _flash.Checked;
            s.SoundEnabled = _sound.Checked;
            s.SoundFile = _soundBuiltIn.Checked ? "" : OneLine(_soundPath.Text);
            s.SoundRepeatSeconds = (int)_repeat.Value;
            s.SoundRepeatForSeconds = (int)_repeatFor.Value;
            s.PhoneProvider = _phone.Checked ? "ntfy" : "none";
            s.NtfyServer = OneLine(_server.Text);
            s.NtfyTopic = OneLine(_topic.Text);
            s.NtfyPriority = Settings.NtfyPriorities[Math.Max(0, _priority.SelectedIndex)];
            s.PhoneMode = _always.Checked ? "always" : "smart";
            s.PhoneAfterSeconds = (int)_after.Value;
            s.PhoneIdleSeconds = (int)_idle.Value;
            return s;
        }

        // Validation plus the two rules that only exist in the dialog: a chosen-file option needs a file.
        public List<SettingProblem> Problems(Settings s)
        {
            List<SettingProblem> list = new List<SettingProblem>();
            if (_logFile.Checked && OneLine(_log.Text).Length == 0) list.Add(new SettingProblem("General.LogPath", "Choose a file, or let the game log be found automatically."));
            if (_sound.Checked && _soundFile.Checked && OneLine(_soundPath.Text).Length == 0) list.Add(new SettingProblem("Sound.File", "Choose a .wav file, or use the built-in sound."));
            foreach (SettingProblem p in s.Validate())
            {
                bool seen = false;
                foreach (SettingProblem q in list) if (q.Key == p.Key) seen = true;
                if (!seen) list.Add(p);
            }
            return list;
        }

        // The same rule on every tab: while an option is off, everything that belongs to it is greyed out,
        // its texts included. A choice among others keeps its words, since it can still be picked; only its
        // fields are greyed out while another choice is picked (and while the option they are in is off).
        void UpdateEnabled()
        {
            Enable(_hotkeyParts, _minimize.Checked);
            Enable(_logFileParts, _logFile.Checked);
            Enable(_popupParts, _popup.Checked);
            Enable(_soundParts, _sound.Checked);
            Enable(_soundFileParts, _sound.Checked && _soundFile.Checked);
            Enable(_phoneParts, _phone.Checked);
            Enable(_smartParts, _phone.Checked && _smart.Checked);
        }

        static void Enable(Control[] parts, bool on)
        {
            foreach (Control c in parts) c.Enabled = on;
        }

        void ClearProblems()
        {
            foreach (Label l in _problems.Values) { l.Text = ""; l.Visible = false; }
            _status.Text = "";
        }

        void ShowProblems(List<SettingProblem> problems)
        {
            ClearProblems();
            bool first = true;
            foreach (SettingProblem p in problems)
            {
                Label l;
                if (_problems.TryGetValue(p.Key, out l)) { l.Text = p.Text; l.Visible = true; }
                if (!first) continue;
                first = false;
                _status.Text = "Please check the highlighted setting.";
                Control field;
                if (_fields.TryGetValue(p.Key, out field))
                {
                    for (Control c = field; c != null; c = c.Parent)
                        if (c is TabPage) { _tabs.SelectedTab = (TabPage)c; break; }
                    field.Focus();
                }
                else _status.Text = p.Text;
            }
        }

        // ---- Apply only when something changed ---------------------------------------------

        // What the dialog showed after loading or the last successful apply. Comparing against this,
        // rather than the settings file, means a hand-written value the dialog shows in its own words
        // (ntfy priority 4 shown as "high") does not count as a change.
        void MarkUnchanged()
        {
            _shownIni = Collect().ToIni();
            _shownAutostart = _autostart.Checked;
            foreach (NumericUpDown n in Numbers()) _shownNumbers[n] = n.Text;
            UpdateApply();
        }

        public bool HasChanges()
        {
            if (_shownIni == null) return false;
            if (_autostart.Checked != _shownAutostart) return true;
            // A number being typed is not in Value until the box is left, and reading Value commits it,
            // clamping it halfway through the typing. So a number whose text differs counts as a change,
            // and Value is only read when every number shows what it showed before.
            foreach (NumericUpDown n in Numbers())
            {
                string shown;
                if (!_shownNumbers.TryGetValue(n, out shown) || n.Text != shown) return true;
            }
            return Collect().ToIni() != _shownIni;
        }

        public bool ApplyEnabled { get { return _applyButton.Enabled; } }

        NumericUpDown[] Numbers() { return new NumericUpDown[] { _repeat, _repeatFor, _after, _idle }; }

        void UpdateApply()
        {
            _applyButton.Enabled = HasChanges();
        }

        // Every input control reports its changes, so the Apply button follows the dialog as it is edited.
        void WatchChanges(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                EventHandler changed = delegate { UpdateApply(); };
                if (c is NumericUpDown) { ((NumericUpDown)c).ValueChanged += changed; c.TextChanged += changed; continue; }
                if (c is TextBox) c.TextChanged += changed;
                else if (c is CheckBox) ((CheckBox)c).CheckedChanged += changed;
                else if (c is RadioButton) ((RadioButton)c).CheckedChanged += changed;
                else if (c is ComboBox) ((ComboBox)c).SelectedIndexChanged += changed;
                WatchChanges(c);
            }
        }

        bool TryApply()
        {
            if (!HasChanges()) { ClearProblems(); return true; }
            Settings s = Collect();
            List<SettingProblem> problems = Problems(s);
            if (problems.Count > 0) { ShowProblems(problems); return false; }
            ClearProblems();
            string error = _apply == null ? null : _apply(s);
            if (error != null) { _status.Text = error; return false; }
            try { if (_autostart.Checked != TrayApp.IsAutostart()) TrayApp.SetAutostart(_autostart.Checked); }
            catch (Exception ex) { _status.Text = "Could not change Start with Windows: " + ex.Message; return false; }
            _base = s;
            MarkUnchanged();
            return true;
        }

        // ---- buttons ---------------------------------------------------------------------

        // Explorer with the file selected (or the folder, if the file is not there yet).
        static void ShowInExplorer(string file)
        {
            try
            {
                if (File.Exists(file)) Process.Start("explorer.exe", "/select,\"" + file + "\"");
                else Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(Path.GetFullPath(file)) + "\"");
            }
            catch (Exception ex) { AppLog.Write("could not open Explorer: {0}", ex.Message); }
        }

        void BrowseLog()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Choose the game's console.log";
                d.Filter = "Game log (console.log)|console.log|All files|*.*";
                string root = LogWatcher.JuvioDocumentsRoot();
                if (Directory.Exists(root)) d.InitialDirectory = root;
                if (d.ShowDialog(this) == DialogResult.OK) _log.Text = d.FileName;
            }
        }

        void BrowseSound()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Choose a sound";
                d.Filter = "Sound (*.wav)|*.wav";
                string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
                if (Directory.Exists(media)) d.InitialDirectory = media;
                if (d.ShowDialog(this) == DialogResult.OK) _soundPath.Text = d.FileName;
            }
        }

        void SendTestPush()
        {
            Settings s = Collect();
            List<SettingProblem> problems = new List<SettingProblem>();
            foreach (SettingProblem p in s.Validate()) if (p.Key.StartsWith("Phone.", StringComparison.Ordinal)) problems.Add(p);
            if (problems.Count > 0) { ShowProblems(problems); return; }
            ClearProblems();
            _testPush.Enabled = false;
            _pushResult.ForeColor = SystemColors.ControlText;
            _pushResult.Text = "Sending...";
            Pusher.SendAsync(s, Texts.TestPushTitle, Texts.TestPushBody,
                delegate(bool ok, string result)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            _testPush.Enabled = _phone.Checked;
                            _pushResult.ForeColor = ok ? SystemColors.ControlText : Color.Firebrick;
                            _pushResult.Text = ok ? "Sent. Check your phone." : "Not sent: " + result;
                        });
                    }
                    catch (Exception) { }   // the dialog was closed meanwhile
                });
        }
    }

    // The dialog's text. It starts exactly at its left edge, as text does in Windows' own dialogs (a plain
    // WinForms label adds a margin that grows with the font), so headings, labels, check boxes and radio
    // buttons share one left edge. Greyed out, it takes Windows' grey for disabled text (GrayText), close
    // to a greyed-out check box's text, which Windows draws a little darker; a plain label would be lighter.
    sealed class DialogLabel : Label
    {
        const TextFormatFlags Flags = TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.NoPadding
            | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;

        // How often any label was asked for its size (once per layout of its table) and how often it really
        // measured its text; the self-test uses them to see that the dialog is not laid out over and over,
        // and that a layout that changes nothing measures nothing.
        internal static int Asked, Measured;

        // The last measurement and what it was made for. A table asks its labels for their size several
        // times per layout, and a tab is laid out again each time it is shown; measuring the text every
        // time made opening the dialog and switching tabs noticeably slower.
        string _measuredText;
        Font _measuredFont;
        int _measuredWidth;
        Size _measuredSize;

        // A step number ("3. ") with a control after it instead of words: exactly as wide as the number
        // and its space, so the control starts where the words of the steps above do. Measured text has a
        // few pixels of slack after its last letter; measuring "3. I" less "I" leaves that out.
        internal bool StepNumber;

        // Wrapped only at MaximumSize (the notes), like an auto-sized label.
        public override Size GetPreferredSize(Size proposedSize)
        {
            Asked++;
            int width = MaximumSize.Width > 0 ? MaximumSize.Width : int.MaxValue;
            if (_measuredFont == null || _measuredText != Text || !_measuredFont.Equals(Font) || _measuredWidth != width)
            {
                Measured++;
                Size text = TextRenderer.MeasureText(Text.Length > 0 ? Text : " ", Font, new Size(width, int.MaxValue), Flags);
                if (Text.Length == 0) text.Width = 0;
                if (StepNumber)
                    text.Width = TextRenderer.MeasureText(Text + "I", Font, new Size(width, int.MaxValue), Flags).Width
                        - TextRenderer.MeasureText("I", Font, new Size(width, int.MaxValue), Flags).Width;
                _measuredText = Text;
                _measuredFont = Font;
                _measuredWidth = width;
                _measuredSize = text;
            }
            return new Size(_measuredSize.Width + Padding.Horizontal, _measuredSize.Height + Padding.Vertical);
        }

        // A part of the text that is a link (ntfy.sh in the Phone tab's setup steps): in Windows' link
        // colour and underlined, with a hand over it. The words around it are drawn exactly as in any other
        // label; a LinkLabel drew its whole line with a weight and spacing of its own. Greyed out, the line
        // is plain grey text throughout. One line only.
        string _link, _url;
        Rectangle _linkBounds;

        internal void SetLink(string link, string url)
        {
            _link = link;
            _url = url;
            Invalidate();
        }

        internal Rectangle LinkBounds { get { return _linkBounds; } }
        internal string LinkUrl { get { return _url; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle r = new Rectangle(Padding.Left, Padding.Top, ClientSize.Width - Padding.Horizontal, ClientSize.Height - Padding.Vertical);
            Color color = Enabled ? ForeColor : SystemColors.GrayText;
            int at = _link == null ? -1 : Text.IndexOf(_link, StringComparison.Ordinal);
            if (at < 0 || !Enabled)
            {
                _linkBounds = Rectangle.Empty;
                TextRenderer.DrawText(e.Graphics, Text, Font, r, color, Flags);
                return;
            }
            const TextFormatFlags piece = TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.NoPadding
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            int x = r.X;
            x += Piece(e.Graphics, Text.Substring(0, at), Font, x, r.Y, color, piece);
            using (Font underlined = new Font(Font, Font.Style | FontStyle.Underline))
            {
                int w = Piece(e.Graphics, _link, underlined, x, r.Y, SystemColors.HotTrack, piece);
                _linkBounds = new Rectangle(x, r.Y, w, r.Height);
                x += w;
            }
            Piece(e.Graphics, Text.Substring(at + _link.Length), Font, x, r.Y, color, piece);
        }

        static int Piece(Graphics g, string text, Font font, int x, int y, Color color, TextFormatFlags flags)
        {
            if (text.Length == 0) return 0;
            TextRenderer.DrawText(g, text, font, new Point(x, y), color, flags);
            return TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue), flags).Width;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = Enabled && _linkBounds.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (Enabled && e.Button == MouseButtons.Left && _linkBounds.Contains(e.Location))
                AppFiles.Open(_url);
        }
    }

    // The tabs, with the selected tab and the edge around the page in the page's own colour. Windows 11
    // fills them with a pale grey that showed against the white pages. Windows still paints the control;
    // that one grey is swapped for white before it reaches the screen, only in the selected tab and below
    // the row of tabs, so the other tabs, hovered or not, keep their own look.
    sealed class SheetTabs : TabControl
    {
        const int WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_PRINTCLIENT = 0x0318;
        const int PRF_ERASEBKGND = 0x08, PRF_CLIENT = 0x04;

        static bool Fixing { get { return Native.IsWindows && Application.RenderWithVisualStyles && !SystemInformation.HighContrast; } }

        protected override void WndProc(ref Message m)
        {
            // The message first: asking for the selected tab sends this window a message of its own.
            bool paint = m.Msg == WM_PAINT || m.Msg == WM_ERASEBKGND || m.Msg == WM_PRINTCLIENT;
            if (paint && Fixing && TabPages.Count > 0 && SelectedIndex >= 0)
            {
                if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }   // painted with the rest, below
                if (m.Msg == WM_PAINT)
                {
                    Native.PAINTSTRUCT ps;
                    IntPtr dc = Native.BeginPaint(Handle, out ps);
                    try { PaintTo(dc); }
                    finally { Native.EndPaint(Handle, ref ps); }
                    m.Result = IntPtr.Zero;
                    return;
                }
                if (m.Msg == WM_PRINTCLIENT) { PaintTo(m.WParam); m.Result = IntPtr.Zero; return; }
            }
            base.WndProc(ref m);
        }

        void PaintTo(IntPtr target)
        {
            Size size = ClientSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            using (Bitmap b = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    IntPtr dc = g.GetHdc();
                    try
                    {
                        Message print = Message.Create(Handle, WM_PRINTCLIENT, dc, (IntPtr)(PRF_CLIENT | PRF_ERASEBKGND));
                        DefWndProc(ref print);
                    }
                    finally { g.ReleaseHdc(dc); }
                }
                Recolor(b);
                using (Graphics g = Graphics.FromHdc(target)) g.DrawImageUnscaled(b, 0, 0);
            }
        }

        // The pane's grey is whatever Windows drew just left of the page; it becomes the page's colour.
        void Recolor(Bitmap b)
        {
            Rectangle page = DisplayRectangle;
            Color pageColor = SelectedTab.BackColor;
            if (page.Left < 1 || page.Top >= b.Height) return;
            int pane = b.GetPixel(page.Left - 1, Math.Min(b.Height - 1, page.Top + page.Height / 2)).ToArgb() & 0xFFFFFF;
            int white = pageColor.ToArgb() & 0xFFFFFF;
            if (pane == white) return;
            Rectangle tab = GetTabRect(SelectedIndex);
            tab.Inflate(2, 2);   // the selected tab is drawn a little larger than its place in the row
            int stripBottom = GetTabRect(0).Bottom;
            BitmapData data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppRgb);
            try
            {
                int[] px = new int[data.Stride / 4 * b.Height];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                int stride = data.Stride / 4;
                for (int y = 0; y < b.Height; y++)
                    for (int x = 0; x < b.Width; x++)
                    {
                        int i = y * stride + x;
                        if ((px[i] & 0xFFFFFF) != pane) continue;
                        if (y >= stripBottom || tab.Contains(x, y)) px[i] = unchecked((int)0xFF000000) | white;
                    }
                Marshal.Copy(px, 0, data.Scan0, px.Length);
            }
            finally { b.UnlockBits(data); }
        }
    }
}
