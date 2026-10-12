using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

namespace HonQueueNotifier
{
    // =====================================================================================
    // Tray application
    // =====================================================================================
    sealed class TrayApp : ApplicationContext, IAlertSink
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "HonQueueNotifier";

        readonly string _iniPath;
        readonly FileStamp _iniStamp;
        readonly NotifyIcon _tray;
        readonly WinTimer _timer;
        readonly SynchronizationContext _ui;
        readonly List<AlertForm> _forms = new List<AlertForm>();
        readonly Icon _windowIcon;
        Icon _trayIcon;
        bool _lightTaskbar;
        readonly ToolStripMenuItem _miTestPush;
        readonly ToolStripMenuItem _miAutostart;

        Settings _settings;
        Win32Probe _probe;
        Core _core;
        bool _busy;
        SettingsForm _settingsForm;
        DateTime _nextIniCheckUtc;
        string _lastError;
        DateTime _lastErrorUtc;

        // The tray icon's tooltip: the program's name and version (Windows allows 63 characters).
        internal static string TooltipText { get { return "HoN Queue Notifier " + Program.Version; } }

        public TrayApp(string iniPath, Settings settings, bool firstRun, bool testAtStart)
        {
            _iniPath = iniPath;
            _iniStamp = new FileStamp(iniPath);
            _settings = settings;

            if (SynchronizationContext.Current == null) SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            _ui = SynchronizationContext.Current;

            ContextMenuStrip menu = BuildMenu(
                delegate { _core.Engine.StartTest(DateTime.UtcNow); },
                delegate { TestPush(); },
                delegate { OpenSettings(); },
                delegate { AppFiles.Open(AppLog.FilePath); },
                delegate { ToggleAutostart(); },
                delegate { Quit(); });
            _miTestPush = (ToolStripMenuItem)menu.Items[1];
            _miAutostart = (ToolStripMenuItem)menu.Items[5];
            menu.Opening += delegate { RefreshMenu(); };

            _windowIcon = AppIcon.Window();
            _lightTaskbar = AppIcon.LightTaskbar();
            _trayIcon = AppIcon.Tray(_lightTaskbar);
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            _tray = new NotifyIcon();
            _tray.Icon = _trayIcon;
            _tray.Text = TooltipText;
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { OpenSettings(); };

            BuildCore();
            _hotkeyWindow = new HotkeyWindow();
            _hotkeyWindow.Pressed += delegate { MinimizePressed(); };

            _timer = new WinTimer();
            _timer.Interval = Core.PollMilliseconds;
            _timer.Tick += OnTick;
            _timer.Start();

            if (firstRun)
                Balloon("HoN Queue Notifier is running", "It lives here in the tray and works as it is. To change how it alerts you, double-click the icon or right-click it and choose Settings.");
            else if (_core.Watcher.Tailers.Count == 0)
                Balloon("HoN Queue Notifier", "No game log found yet under Documents\\Juvio. It will be picked up when the game starts.");
            if (testAtStart) _core.Engine.StartTest(DateTime.UtcNow);
        }

        // The tray menu. No status line: the log's last phase is not the game's current state
        // (it stays "in the menu" after the game is closed), so it misled more than it helped.
        public static ContextMenuStrip BuildMenu(EventHandler testAlert, EventHandler testPush, EventHandler settings,
            EventHandler openLog, EventHandler autostart, EventHandler exit)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Show a test alert", null, testAlert));
            menu.Items.Add(new ToolStripMenuItem("Send a test notification to my phone", null, testPush));
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem miSettings = new ToolStripMenuItem("Settings...", null, settings);
            miSettings.Font = new Font(miSettings.Font, FontStyle.Bold);   // what a double-click on the icon does
            menu.Items.Add(miSettings);
            menu.Items.Add(new ToolStripMenuItem("Open this program's log", null, openLog));
            menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, autostart));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, exit));
            return menu;
        }

        void BuildCore()
        {
            _probe = new Win32Probe();
            _core = new Core(_settings, _probe, this);
            if (_settings.PhoneEnabled) Pusher.WarmUp(_settings);
            try { _core.Step(DateTime.UtcNow); }
            catch (Exception ex) { AppLog.Write("first step failed: {0}", ex); }
        }

        // The taskbar switched between light and dark: the tray icon follows, like the icons beside it.
        void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            try
            {
                bool light = AppIcon.LightTaskbar();
                if (light == _lightTaskbar) return;
                _lightTaskbar = light;
                Icon old = _trayIcon;
                _trayIcon = AppIcon.Tray(light);
                _tray.Icon = _trayIcon;
                old.Dispose();
                AppLog.Write("taskbar is now {0}: tray icon redrawn", light ? "light" : "dark");
            }
            catch (Exception ex) { AppLog.Write("could not redraw the tray icon: {0}", ex.Message); }
        }

        void OnTick(object sender, EventArgs e)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                DateTime now = DateTime.UtcNow;
                _core.Step(now);
                UpdateHotkey();
                if (now >= _nextIniCheckUtc || now < _nextIniCheckUtc.AddSeconds(-10))
                {
                    _nextIniCheckUtc = now.AddSeconds(2);
                    if (_iniStamp.Changed()) ReloadFromDisk();
                }
            }
            catch (Exception ex)
            {
                // The same failure every quarter of a second would bury everything else in the log.
                string text = ex.ToString();
                if (text != _lastError || (DateTime.UtcNow - _lastErrorUtc).TotalMinutes >= 1)
                {
                    _lastError = text;
                    _lastErrorUtc = DateTime.UtcNow;
                    AppLog.Write("tick failed: {0}", text);
                }
            }
            finally { _busy = false; }
        }

        void RefreshMenu()
        {
            _miTestPush.Enabled = _settings.PhoneEnabled;
            try { _miAutostart.Checked = IsAutostart(); } catch (Exception) { }
        }

        void Balloon(string title, string text)
        {
            try { _tray.ShowBalloonTip(6000, title, text, ToolTipIcon.Info); } catch (Exception) { }
        }

        // ---- the minimize hotkey: registered only while the game has focus -------------------

        HotkeyWindow _hotkeyWindow;
        readonly HotkeyState _hotkeyState = new HotkeyState();
        bool _hotkeyFailLogged;

        void UpdateHotkey()
        {
            if (_hotkeyWindow == null) return;
            uint modifiers;
            Keys key;
            bool valid = HotkeyText.TryParse(_settings.MinimizeHotkey, out modifiers, out key);
            bool enabled = _settings.MinimizeHotkeyEnabled;
            bool foreground = enabled && valid && _probe.IsGameForeground();
            int step = _hotkeyState.Step(enabled, valid, foreground);
            if (step > 0)
            {
                bool ok = _hotkeyWindow.Register(modifiers, key);
                _hotkeyState.Done(ok);
                if (ok) AppLog.Write("minimize hotkey {0} active: the game has focus", HotkeyText.Format(modifiers, key));
                if (!ok && !_hotkeyFailLogged)
                {
                    _hotkeyFailLogged = true;
                    AppLog.Write("could not register the minimize hotkey {0} (another program may use it): error {1}", _settings.MinimizeHotkey, Marshal.GetLastWin32Error());
                }
            }
            else if (step < 0)
            {
                _hotkeyWindow.Unregister();
                _hotkeyState.Done(false);
                AppLog.Write("minimize hotkey released: the game no longer has focus");
            }
        }

        void MinimizePressed()
        {
            if (!_probe.IsGameForeground()) { AppLog.Write("minimize hotkey pressed, but the game no longer has focus"); return; }
            bool ok = _probe.MinimizeGame();
            AppLog.Write("minimize hotkey pressed, game minimized: {0}", ok);
        }

        void Use(Settings s)
        {
            HideAlert();
            if (_hotkeyWindow != null) { _hotkeyWindow.Unregister(); _hotkeyState.Done(false); _hotkeyFailLogged = false; }
            _settings = s;
            BuildCore();
        }

        // The settings file was changed by hand (or by another copy of the program).
        void ReloadFromDisk()
        {
            if (!File.Exists(_iniPath)) return;
            List<string> warnings = new List<string>();
            Settings s;
            try { s = Settings.Load(_iniPath, warnings); }
            catch (Exception ex)
            {
                AppLog.Write("could not read settings: {0}", ex.Message);
                Balloon("Settings not reloaded", ex.Message);
                return;
            }
            for (int i = 0; i < warnings.Count; i++) AppLog.Write("settings: {0}", warnings[i]);
            Use(s);
            AppLog.Write("settings file changed, reloaded");
            Balloon("Settings reloaded", warnings.Count == 0 ? "The settings file was changed and has been read again." : warnings.Count.ToString(CultureInfo.InvariantCulture) + " problem(s): " + warnings[0]);
        }

        string ApplySettings(Settings s)
        {
            try
            {
                s.Save(_iniPath);
                _iniStamp.Take();
            }
            catch (Exception ex)
            {
                AppLog.Write("could not save settings: {0}", ex.Message);
                return "Could not save the settings: " + ex.Message;
            }
            Use(s);
            AppLog.Write("settings saved from the dialog");
            return null;
        }

        void OpenSettings()
        {
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                if (_settingsForm.WindowState == FormWindowState.Minimized) _settingsForm.WindowState = FormWindowState.Normal;
                _settingsForm.Activate();
                return;
            }
            _settingsForm = new SettingsForm(_settings, _windowIcon, ApplySettings, delegate { _core.Engine.StartTest(DateTime.UtcNow); }, _iniPath, AppLog.FilePath);
            _settingsForm.FormClosed += delegate { _settingsForm.Dispose(); _settingsForm = null; };
            _settingsForm.Show();
            _settingsForm.Activate();
        }

        void TestPush()
        {
            Pusher.SendAsync(_settings, Texts.TestPushTitle, Texts.TestPushBody,
                delegate(bool ok, string result)
                {
                    _ui.Post(delegate { Balloon(ok ? "Test notification sent" : "Test notification failed", result); }, null);
                });
        }

        public static bool IsAutostart()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                return key != null && key.GetValue(RunValue) != null;
        }

        public static void SetAutostart(bool on)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(RunValue, false);
            }
            AppLog.Write("start with Windows: {0}", on ? "on" : "off");
        }

        void ToggleAutostart()
        {
            try { SetAutostart(!IsAutostart()); }
            catch (Exception ex) { AppLog.Write("could not change autostart: {0}", ex.Message); }
        }

        void Quit()
        {
            try { if (_settingsForm != null) _settingsForm.Close(); } catch (Exception) { }
            try { if (_hotkeyWindow != null) _hotkeyWindow.Dispose(); } catch (Exception) { }
            SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;   // a static event: it would keep us alive
            try { _timer.Stop(); HideAlert(); _tray.Visible = false; _tray.Dispose(); } catch (Exception) { }
            ExitThread();
        }

        // ---- IAlertSink ----------------------------------------------------------------

        static float SystemScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return Math.Max(1f, g.DpiX / 96f);
            }
            catch (Exception) { return 1f; }
        }

        List<Screen> TargetScreens()
        {
            List<Screen> list = new List<Screen>();
            string mode = (_settings.PopupScreens ?? "").Trim().ToLowerInvariant();
            if (mode == "primary") list.Add(Screen.PrimaryScreen);
            else if (mode == "cursor") list.Add(Screen.FromPoint(Cursor.Position));
            else list.AddRange(Screen.AllScreens);
            if (list.Count == 0) list.Add(Screen.PrimaryScreen);
            return list;
        }

        public void ShowAlert(string title, AlertStage stage, MatchTracker tracker, DateTime sinceUtc)
        {
            if (_forms.Count == 0)
            {
                float scale = SystemScale();
                foreach (Screen screen in TargetScreens())
                {
                    AlertForm f = new AlertForm(screen.WorkingArea, _settings.PopupPosition, scale);
                    f.OpenRequested += delegate
                    {
                        // A notice about a match that is already gone is only dismissed; there is nothing to rush to.
                        if (_core.Engine.Active != AlertStage.Over)
                        {
                            bool ok = _probe.BringGameToFront();
                            AppLog.Write("popup clicked, game brought to front: {0}", ok);
                        }
                        _core.Engine.Acknowledge("popup clicked");
                    };
                    f.CloseRequested += delegate { _core.Engine.Acknowledge("popup closed"); };
                    _forms.Add(f);
                }
            }
            string map = tracker == null ? null : Texts.MapName(tracker.World);
            for (int i = 0; i < _forms.Count; i++)
            {
                _forms[i].SetContent(title, stage, _core.Engine.Notice, _core.Engine.IsTest, map, sinceUtc);
                if (!_forms[i].Visible) _forms[i].Show();
            }
        }

        public void HideAlert()
        {
            for (int i = 0; i < _forms.Count; i++)
            {
                try { _forms[i].Hide(); _forms[i].Dispose(); } catch (Exception) { }
            }
            _forms.Clear();
            try { _probe.FlashGame(false); } catch (Exception) { }
        }

        public void PlaySound() { Chime.Play(_settings.SoundFile); }

        public void FlashGame()
        {
            try { _probe.FlashGame(true); } catch (Exception ex) { AppLog.Write("could not flash the taskbar: {0}", ex.Message); }
        }

        public void SendPush(AlertStage stage, MatchTracker tracker)
        {
            Pusher.SendAsync(_settings, Texts.PushTitle(stage), Texts.PushBody(stage, tracker), null);
        }
    }
}
