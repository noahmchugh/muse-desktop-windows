using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MuseDesktop;

/// <summary>
/// A single window that hosts muse.ai, the same approach as the official
/// Mac app (the web app inside a native window). Login cookies persist in
/// the user's LocalAppData so sign-in survives restarts.
/// </summary>
public sealed class MainForm : Form
{
    private const string HomeUrl = "https://muse.ai";

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    private readonly string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuseDesktop");

    private string SettingsPath => Path.Combine(_dataDir, "window.json");
    private string AppSettingsPath => Path.Combine(_dataDir, "settings.json");

    // Global hotkey plumbing (see AppSettings.cs). Ids are arbitrary;
    // they come back in WndProc as the WM_HOTKEY wParam.
    private const int WmHotkey = 0x0312;
    private const int HotkeyBringToFrontId = 1;
    private const int HotkeyMuteId = 2;

    private AppSettings _settings = new();
    private bool _hotkeysRegistered;
    private ToolStripMenuItem? _hotkeysItem;
    private ToolStripMenuItem? _startOnLoginItem;

    private BridgeServer? _bridge;
    private NotifyIcon? _tray;
    private ExpressForm? _expressForm;

    // The Standard shell: one slim top bar, no rail, no address bar.
    private ChromePalette _palette = Chrome.For("Dark");
    private Panel _topBar = null!;
    private Panel _hairline = null!;
    private Label _wordmark = null!;
    private Button _btnSettings = null!;
    private Button _btnMax = null!;
    private Button _btnClose = null!;
    private Panel _bridgeDot = null!;
    private Label _bridgeLabel = null!;
    private Color _dotColor = Chrome.StatusOff;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 2000 };
    private SettingsForm? _settingsForm;

    public MainForm()
    {
        Text = "Muse";
        Width = 1240;
        Height = 820;
        MinimumSize = new Size(480, 360);
        // Borderless: the app draws its own slim top bar with the
        // caption buttons in it (the standard desktop-app shell).
        // The form's padding leaves a thin frame of its own surface
        // around the content, which doubles as the resize grip.
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(Chrome.BorderWidth, 0, Chrome.BorderWidth, Chrome.BorderWidth);
        Controls.Add(_web);

        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        _settings = AppSettings.Load(AppSettingsPath);
        _palette = Chrome.For(_settings.FrameShade);
        BackColor = _palette.Border;
        BuildTopBar();
        StartBridge();
        ApplyStartOnLogin();
        TopMost = _settings.AlwaysOnTop;
        SetupTray();
        _statusTimer.Tick += (_, _) => UpdateBridgeStatus();
        _statusTimer.Start();
        UpdateBridgeStatus();

        RestoreSavedBounds();

        Load += OnLoad;
        FormClosing += OnClosing;
        Resize += OnResize;
    }

    // Minimizing tucks the window into the tray instead of the
    // taskbar. The form is only hidden, never closed or disposed,
    // so the WebView session — including a live voice call — keeps
    // running untouched. The tray icon (or a second launch) brings
    // it back through Surface().
    private void OnResize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            if (_settings.MinimizeToTray)
            {
                Hide();
            }
            return;
        }
        // A maximized borderless window drops the resize frame so
        // the page reaches the screen edges.
        Padding = WindowState == FormWindowState.Maximized
            ? new Padding(0)
            : new Padding(Chrome.BorderWidth, 0, Chrome.BorderWidth, Chrome.BorderWidth);
        if (_btnMax is not null)
        {
            _btnMax.Text = WindowState == FormWindowState.Maximized ? "\uE923" : "\uE922";
        }
    }

    // Called by Program when a second launch asks this instance to
    // come to the front instead of starting alongside it.
    public void Surface()
    {
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Show();
        Activate();
        BringToFront();
    }

    // ---------- The Standard shell ----------
    // One slim top bar and nothing else: no sidebar, no address
    // bar. Navigation lives on the keyboard (Alt+Left / Alt+Right /
    // Alt+Home / F5, see ProcessCmdKey) and the bridge's everyday
    // state sits in the bar where it can be seen at a glance.

    private void BuildTopBar()
    {
        var tips = new ToolTip();
        _topBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = _palette.Bar,
        };

        // Left: the app's own mark and name.
        var iconBox = new PictureBox
        {
            Location = new Point(11, 10),
            Size = new Size(20, 20),
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        try { iconBox.Image = Icon?.ToBitmap(); } catch { }
        _topBar.Controls.Add(iconBox);
        _wordmark = new Label
        {
            Text = "Muse",
            Location = new Point(39, 0),
            Height = 40,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = _palette.Text,
            Font = new Font("Segoe UI", 10.5f),
        };
        _topBar.Controls.Add(_wordmark);

        // Right cluster, laid out from the right edge: caption
        // buttons, then Settings, then the bridge status.
        Button CaptionButton(string glyph, string tip)
        {
            var btn = new Button
            {
                Text = glyph,
                Size = new Size(46, 40),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe MDL2 Assets", 10f),
                ForeColor = _palette.Text,
                BackColor = Color.Transparent,
                TabStop = false,
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = _palette.Hover;
            btn.FlatAppearance.MouseDownBackColor = _palette.Field;
            tips.SetToolTip(btn, tip);
            return btn;
        }

        var btnMin = CaptionButton("\uE921", "Minimize");
        btnMin.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _btnMax = CaptionButton("\uE922", "Maximize");
        _btnMax.Click += (_, _) => ToggleMaximize();
        _btnClose = CaptionButton("\uE8BB", "Close");
        _btnClose.FlatAppearance.MouseOverBackColor = Chrome.CloseHover;
        _btnClose.Click += (_, _) => Close();

        _btnSettings = new Button
        {
            Text = "\uE713",
            Size = new Size(40, 40),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe MDL2 Assets", 13f),
            ForeColor = _palette.Text,
            BackColor = Color.Transparent,
            TabStop = false,
        };
        _btnSettings.FlatAppearance.BorderSize = 0;
        _btnSettings.FlatAppearance.MouseOverBackColor = _palette.Hover;
        _btnSettings.FlatAppearance.MouseDownBackColor = _palette.Field;
        tips.SetToolTip(_btnSettings, "Settings");
        _btnSettings.Click += (_, _) => ShowSettings();

        // Bridge status: a dot and a word. Green on, amber paused,
        // red error, gray off. Clicking opens the full status.
        _bridgeDot = new Panel { Size = new Size(10, 10) };
        _bridgeDot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(_dotColor);
            e.Graphics.FillEllipse(brush, 0, 0, 9, 9);
        };
        _bridgeLabel = new Label
        {
            Text = "Bridge off",
            Height = 40,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = _palette.SubText,
            Font = new Font("Segoe UI", 9.5f),
            Cursor = Cursors.Hand,
        };
        tips.SetToolTip(_bridgeLabel, "Bridge status — click for details");
        tips.SetToolTip(_bridgeDot, "Bridge status — click for details");
        _bridgeLabel.Click += (_, _) => ShowBridgeStatus();
        _bridgeDot.Click += (_, _) => ShowBridgeStatus();

        _hairline = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 1,
            BackColor = _palette.Border,
        };
        _topBar.Controls.Add(_hairline);

        _topBar.Resize += (_, _) => LayoutTopBarRight();
        Controls.Add(_topBar);
        LayoutTopBarRight();

        // The bar is the window's handle: drag to move, double-click
        // to maximize, drag the very top edge to resize upward.
        AttachDrag(_topBar);
        AttachDrag(_wordmark);
        AttachDrag(iconBox);

        _topBar.Controls.Add(btnMin);
        _topBar.Controls.Add(_btnMax);
        _topBar.Controls.Add(_btnClose);
        _topBar.Controls.Add(_btnSettings);
        _topBar.Controls.Add(_bridgeLabel);
        _topBar.Controls.Add(_bridgeDot);

        // Positions only. Controls are added once, above — churning
        // membership or z-order on every resize is wasted work and
        // can feed layout loops.
        void LayoutTopBarRight()
        {
            var x = _topBar.Width;
            foreach (var btn in new[] { _btnClose, _btnMax, btnMin })
            {
                x -= btn.Width;
                btn.Location = new Point(x, 0);
            }
            x -= _btnSettings.Width;
            _btnSettings.Location = new Point(x, 0);
            var labelWidth = _bridgeLabel.PreferredWidth;
            _bridgeLabel.Location = new Point(x - labelWidth - 8, 0);
            _bridgeDot.Location = new Point(x - labelWidth - 8 - 16, 15);
        }
    }

    private void AttachDrag(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }
            if (e.Clicks == 2)
            {
                ToggleMaximize();
                return;
            }
            // The top few pixels of the bar resize the window upward
            // instead of dragging it.
            var dragPoint = control.PointToScreen(e.Location);
            var formPoint = PointToClient(dragPoint);
            var ht = 2; // HTCAPTION
            if (WindowState == FormWindowState.Normal && formPoint.Y <= 4)
            {
                ht = formPoint.X < 16 ? 13 : formPoint.X > Width - 16 ? 14 : 12;
            }
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, (IntPtr)ht, IntPtr.Zero);
        };
    }

    private void ToggleMaximize()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
        }
        else
        {
            MaximizedBounds = Screen.FromControl(this).WorkingArea;
            WindowState = FormWindowState.Maximized;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // Recolors the chrome after the frame shade changes in Settings.
    private void ApplyChrome()
    {
        _palette = Chrome.For(_settings.FrameShade);
        BackColor = _palette.Border;
        _topBar.BackColor = _palette.Bar;
        _hairline.BackColor = _palette.Border;
        _wordmark.ForeColor = _palette.Text;
        _bridgeLabel.ForeColor = _palette.SubText;
        foreach (var btn in _topBar.Controls.OfType<Button>())
        {
            btn.ForeColor = _palette.Text;
            btn.FlatAppearance.MouseOverBackColor =
                btn == _btnClose ? Chrome.CloseHover : _palette.Hover;
            btn.FlatAppearance.MouseDownBackColor = _palette.Field;
        }
    }

    // The WebView events still call this on every navigation; the
    // nav buttons and URL it used to refresh are gone, so it now
    // only keeps the bridge status current.
    private void UpdateNavState()
    {
        UpdateBridgeStatus();
    }

    private string _lastBridgeText = "";

    // Called from WebView events (which can fire in bursts during
    // page activity) and a 2-second timer. It must be free when
    // nothing changed: the first version touched the label, the
    // dot, and the tray icon on every call.
    private void UpdateBridgeStatus()
    {
        if (_bridgeLabel is null)
        {
            return;
        }
        string text;
        Color color;
        if (_bridge is null)
        {
            text = "Bridge off";
            color = Chrome.StatusOff;
        }
        else if (_bridge.Paused)
        {
            text = "Bridge paused";
            color = Chrome.StatusAmber;
        }
        else if (_bridge.Running)
        {
            text = "Bridge on";
            color = Chrome.StatusGreen;
        }
        else
        {
            text = "Bridge error";
            color = Chrome.StatusRed;
        }
        if (text == _lastBridgeText)
        {
            return;
        }
        _lastBridgeText = text;
        _dotColor = color;
        _bridgeLabel.Text = text;
        if (_btnSettings is not null)
        {
            _bridgeLabel.Location = new Point(
                _btnSettings.Left - _bridgeLabel.PreferredWidth - 8, 0);
            _bridgeDot.Location = new Point(_bridgeLabel.Left - 16, 15);
        }
        _bridgeDot.Invalidate();
        UpdateTrayText();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        try
        {
            if (keyData == (Keys.Alt | Keys.Left))
            {
                if (_web.CanGoBack) _web.GoBack();
                return true;
            }
            if (keyData == (Keys.Alt | Keys.Right))
            {
                if (_web.CanGoForward) _web.GoForward();
                return true;
            }
            if (keyData == (Keys.Alt | Keys.Home))
            {
                _web.Source = new Uri(HomeUrl);
                return true;
            }
            if (keyData == Keys.F5)
            {
                _web.Reload();
                return true;
            }
        }
        catch { /* navigation shortcuts are a nicety */ }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------- Global hotkeys ----------

    protected override void WndProc(ref Message m)
    {
        // Borderless resize: the form's own frame (its padding) is
        // the grip. Child controls cover the rest of the window, so
        // a client-area hit here is always on that frame.
        if (m.Msg == 0x0084) // WM_NCHITTEST
        {
            base.WndProc(ref m);
            if (m.Result.ToInt32() == 1 && WindowState == FormWindowState.Normal)
            {
                var lp = m.LParam.ToInt32();
                var screen = new Point(
                    unchecked((short)(lp & 0xFFFF)),
                    unchecked((short)(lp >> 16)));
                var p = PointToClient(screen);
                var grip = Chrome.BorderWidth + 2;
                var bottom = p.Y >= Height - grip;
                var left = p.X < grip;
                var right = p.X >= Width - grip;
                var hit = bottom && left ? 16 // HTBOTTOMLEFT
                    : bottom && right ? 17 // HTBOTTOMRIGHT
                    : bottom ? 15 // HTBOTTOM
                    : left ? 10 // HTLEFT
                    : right ? 11 // HTRIGHT
                    : 0;
                if (hit != 0)
                {
                    m.Result = (IntPtr)hit;
                }
            }
            return;
        }
        if (m.Msg == WmHotkey)
        {
            var id = m.WParam.ToInt32();
            if (id == HotkeyBringToFrontId)
            {
                Surface();
                return;
            }
            if (id == HotkeyMuteId)
            {
                _ = ToggleMuteAsync();
                return;
            }
        }
        base.WndProc(ref m);
    }

    // Registration happens only when the user has enabled hotkeys;
    // a combination another app already owns simply fails to
    // register and the other one still can.
    private void RegisterConfiguredHotkeys()
    {
        UnregisterConfiguredHotkeys();
        if (!_settings.HotkeysEnabled)
        {
            return;
        }
        var front = GlobalHotkey.Register(Handle, HotkeyBringToFrontId, _settings.BringToFrontHotkey);
        var mute = GlobalHotkey.Register(Handle, HotkeyMuteId, _settings.MuteHotkey);
        _hotkeysRegistered = front || mute;
    }

    private void UnregisterConfiguredHotkeys()
    {
        if (!_hotkeysRegistered)
        {
            return;
        }
        GlobalHotkey.Unregister(Handle, HotkeyBringToFrontId);
        GlobalHotkey.Unregister(Handle, HotkeyMuteId);
        _hotkeysRegistered = false;
    }

    private const string MuteScript = """
        (() => {
          const els = document.querySelectorAll('button, [role="button"]');
          for (const el of els) {
            const label = ((el.getAttribute('aria-label') || '') + ' ' + (el.title || '')).toLowerCase();
            if (label.includes('mute') && !label.includes('notification')) {
              el.click();
              return 'toggled';
            }
          }
          return 'not-found';
        })()
        """;

    // The mute toggle behind the global hotkey and the tray entry.
    // It clicks the page's own microphone button — a button whose
    // label mentions muting. If no such control can be found (the
    // page changed, or no call controls are on screen), it surfaces
    // the window instead, so the key press is never a silent no-op.
    private async Task ToggleMuteAsync()
    {
        try
        {
            var core = _web.CoreWebView2;
            if (core is not null)
            {
                var result = await core.ExecuteScriptAsync(MuteScript);
                if (result is not null && !result.Contains("not-found"))
                {
                    return;
                }
            }
        }
        catch { /* fall through to surfacing the window */ }
        Surface();
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        RegisterConfiguredHotkeys();
        try
        {
            Directory.CreateDirectory(_dataDir);
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: Path.Combine(_dataDir, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;

            // Decide permission requests (microphone for voice chat,
            // camera, location, ...) ourselves instead of leaving the
            // WebView default in charge.
            core.PermissionRequested += OnPermissionRequested;

            // Links that would open a new window stay inside the app
            // instead — but only for Muse and its sign-in hosts. Any
            // other site opens in the user's default browser rather
            // than inside this window's signed-in profile.
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (string.IsNullOrEmpty(args.Uri))
                {
                    return;
                }
                if (WebNavigation.MayNavigateInApp(args.Uri, WebNavigation.IsMuseHost))
                {
                    core.Navigate(args.Uri);
                }
                else
                {
                    LogDivertedNavigation("new-window", args.Uri);
                    WebNavigation.OpenInDefaultBrowser(args.Uri);
                }
            };

            // Same allowlist for in-window navigation: following a link
            // out of Muse cancels the navigation here and opens the
            // page in the default browser instead.
            core.NavigationStarting += (_, args) =>
            {
                if (!WebNavigation.MayNavigateInApp(args.Uri, WebNavigation.IsMuseHost))
                {
                    args.Cancel = true;
                    LogDivertedNavigation("navigation", args.Uri);
                    WebNavigation.OpenInDefaultBrowser(args.Uri);
                }
            };

            // Keep the nav bar's back/forward state and URL current.
            core.NavigationStarting += (_, _) => UpdateNavState();
            core.NavigationCompleted += OnNavigationCompleted;
            core.ProcessFailed += OnWebViewProcessFailed;
            core.SourceChanged += (_, _) => UpdateNavState();
            core.HistoryChanged += (_, _) => UpdateNavState();

            _web.Source = new Uri(HomeUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Muse needs the Microsoft Edge WebView2 Runtime. It ships with " +
                "Windows 11, so if you see this, install it from " +
                "https://developer.microsoft.com/microsoft-edge/webview2/ " +
                "and try again.\n\nDetails: " + ex.Message,
                "Muse",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    // Every navigation the allowlist diverts to the default browser is
    // recorded in the data folder. A sign-in flow that gains a new host
    // otherwise fails silently: the window just sits blank while the
    // sign-in opens elsewhere. This log names the host that was missed
    // so it can be added to WebNavigation.
    private void LogDivertedNavigation(string kind, string? uri)
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            File.AppendAllText(
                Path.Combine(_dataDir, "nav-divert.log"),
                $"{DateTimeOffset.Now:u} {kind} {uri}\n");
        }
        catch { /* logging must never break navigation handling */ }
    }

    // The only permission this window grants is the microphone, and
    // only to Muse itself (voice chat). Everything else — camera,
    // location, notifications, and any permission asked for by another
    // origin — is denied. SavesInProfile stores the decision in the
    // WebView profile, so the choice is made once instead of the
    // prompt coming back every session.
    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        var allow = args.PermissionKind == CoreWebView2PermissionKind.Microphone
            && Uri.TryCreate(args.Uri, UriKind.Absolute, out var origin)
            && WebNavigation.IsMuseHost(origin.Host);
        args.State = allow
            ? CoreWebView2PermissionState.Allow
            : CoreWebView2PermissionState.Deny;
        args.SavesInProfile = true;
    }

    // ---------- Reconnect resilience ----------

    private string? _lastGoodUrl;
    private bool _restoreAttempted;

    // Every successful load of a Muse page becomes the place to
    // return to. A failed navigation (network drop, a reload that
    // lands wrong) restores it once; if the restore also fails the
    // flag stays set, so a dead network cannot spin a reload loop.
    // The next successful load clears the flag and moves the mark.
    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        UpdateNavState();
        var core = _web.CoreWebView2;
        if (core is null)
        {
            return;
        }
        if (args.IsSuccess)
        {
            _restoreAttempted = false;
            var source = core.Source;
            if (!string.IsNullOrEmpty(source)
                && WebNavigation.MayNavigateInApp(source, WebNavigation.IsMuseHost))
            {
                _lastGoodUrl = source;
            }
            return;
        }
        // A navigation the user (or a newer navigation) cancelled is
        // not a failure to recover from.
        if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            RestoreLastGoodPage(core);
        }
    }

    private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        // Only a dead browser or renderer process leaves the page
        // unusable; frame-level failures recover inside the page.
        if (args.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited
            && args.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            return;
        }
        var core = _web.CoreWebView2;
        if (core is not null)
        {
            RestoreLastGoodPage(core);
        }
    }

    private void RestoreLastGoodPage(CoreWebView2 core)
    {
        if (_restoreAttempted)
        {
            return;
        }
        _restoreAttempted = true;
        try
        {
            core.Navigate(_lastGoodUrl ?? HomeUrl);
        }
        catch { /* a later successful load resets the flag */ }
    }

    // ---------- Muse Bridge (v2) ----------

    private void StartBridge()
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            _bridge?.Dispose();
            _bridge = new BridgeServer(_dataDir);
            BridgeServer.UiThreadInvoke = action => BeginInvoke(action);
            _bridge.Start();

            BridgeApps.ExpressOpenWindow = () => BeginInvoke(new Action(ShowExpressWindow));
            BridgeApps.ExpressEvalJs = script =>
            {
                var tcs = new TaskCompletionSource<string>();
                BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        ShowExpressWindow();
                        tcs.TrySetResult(await _expressForm!.EvalAsync(script));
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetResult("{\"error\":" + JsonSerializer.Serialize(ex.Message) + "}");
                    }
                }));
                return tcs.Task;
            };
            BridgeApps.ExpressWindowInfo = () =>
                _expressForm?.Info() ?? new { ready = false, note = "express window not opened yet" };
        }
        catch (Exception ex)
        {
            // The chat window matters more than the bridge; report, don't die.
            LastBridgeError = ex.Message;
        }
    }

    private string? LastBridgeError { get; set; }

    // ---------- Settings plumbing (used by SettingsForm) ----------

    internal AppSettings Settings => _settings;

    internal BridgeServer? Bridge => _bridge;

    internal string? BridgeError => LastBridgeError ?? _bridge?.LastError;

    internal string BridgeLogPath => Path.Combine(_dataDir, "bridge-log.jsonl");

    // One funnel for every preference change: persist, then apply
    // each live effect (hotkeys, login entry, topmost, chrome), so
    // the tray toggles and the Settings form can never drift apart.
    internal void ApplySettings()
    {
        _settings.Save(AppSettingsPath);
        RegisterConfiguredHotkeys();
        ApplyStartOnLogin();
        TopMost = _settings.AlwaysOnTop;
        ApplyChrome();
        if (_hotkeysItem is not null) _hotkeysItem.Checked = _settings.HotkeysEnabled;
        if (_startOnLoginItem is not null) _startOnLoginItem.Checked = _settings.StartOnLogin;
        UpdateBridgeStatus();
    }

    internal void ResetSettings()
    {
        _settings = new AppSettings();
        ApplySettings();
    }

    internal void RefreshBridgeStatus() => UpdateBridgeStatus();

    internal void RestartBridge()
    {
        try
        {
            _bridge?.Dispose();
            _bridge = null;
            // Give the listener a beat to release the port before
            // the new server binds it.
            System.Threading.Thread.Sleep(200);
            StartBridge();
        }
        catch (Exception ex)
        {
            LastBridgeError = ex.Message;
        }
        UpdateBridgeStatus();
    }

    private void ShowSettings()
    {
        if (_settingsForm is not null && !_settingsForm.IsDisposed)
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(this);
        _settingsForm.Show(this);
    }

    // ---------- Start on login (opt-in) ----------

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Muse";

    // Keeps the per-user Run key in line with the setting: the value
    // exists only while the user has the toggle on, and points at
    // wherever the app currently runs from. Runs at startup too, so
    // moving the app heals the entry instead of stranding it.
    private void ApplyStartOnLogin()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }
            if (_settings.StartOnLogin)
            {
                key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // A failed preference must never take the app down.
        }
    }

    private void ShowExpressWindow()
    {
        _expressForm ??= new ExpressForm(_dataDir);
        if (!_expressForm.Visible)
        {
            _expressForm.Show();
        }
        _expressForm.Activate();
    }

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Muse", null, (_, _) => Surface());
        menu.Items.Add("Mute / unmute", null, (_, _) => _ = ToggleMuteAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Bridge status…", null, (_, _) => ShowBridgeStatus());
        menu.Items.Add("Open Adobe Express window", null, (_, _) => ShowExpressWindow());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        var pauseItem = new ToolStripMenuItem("Pause bridge");
        pauseItem.Click += (_, _) =>
        {
            if (_bridge is null) return;
            _bridge.Paused = !_bridge.Paused;
            pauseItem.Text = _bridge.Paused ? "Resume bridge" : "Pause bridge";
            UpdateTrayText();
        };
        menu.Items.Add(pauseItem);
        _hotkeysItem = new ToolStripMenuItem("Enable global hotkeys")
        {
            Checked = _settings.HotkeysEnabled,
        };
        _hotkeysItem.Click += (_, _) =>
        {
            _settings.HotkeysEnabled = !_settings.HotkeysEnabled;
            _hotkeysItem.Checked = _settings.HotkeysEnabled;
            _settings.Save(AppSettingsPath);
            RegisterConfiguredHotkeys();
        };
        menu.Items.Add(_hotkeysItem);
        _startOnLoginItem = new ToolStripMenuItem("Start on login")
        {
            Checked = _settings.StartOnLogin,
        };
        _startOnLoginItem.Click += (_, _) =>
        {
            _settings.StartOnLogin = !_settings.StartOnLogin;
            _startOnLoginItem.Checked = _settings.StartOnLogin;
            _settings.Save(AppSettingsPath);
            ApplyStartOnLogin();
        };
        menu.Items.Add(_startOnLoginItem);
        menu.Items.Add("Quit Muse", null, (_, _) => Close());

        _tray = new NotifyIcon
        {
            Icon = Icon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => Surface();
        UpdateTrayText();
    }

    private void UpdateTrayText()
    {
        if (_tray is null) return;
        var state = _bridge is null ? "off"
            : _bridge.Paused ? "paused"
            : _bridge.Running ? $"on :{BridgeServer.Port}" : "error";
        _tray.Text = "Muse — bridge " + state;
    }

    private void ShowBridgeStatus()
    {
        var addresses = new List<string> { $"http://127.0.0.1:{BridgeServer.Port}" };
        if (_bridge is not null)
        {
            // Reuse the bridge's own /info data via a fresh server probe is
            // overkill; read tailnet addresses the same way BridgeServer does.
        }
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var b = addr.Address.GetAddressBytes();
                    if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                    {
                        addresses.Add($"http://{addr.Address}:{BridgeServer.Port}");
                    }
                }
            }
        }
        catch { }

        var form = new Form
        {
            Text = "Muse Bridge",
            Width = 560,
            Height = 430,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
        };
        var label = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            Text =
                "Bridge: " + (_bridge?.Running == true ? "running" : "NOT running") +
                (_bridge?.Paused == true ? " (paused)" : "") + "\n" +
                (LastBridgeError is not null ? "Error: " + LastBridgeError + "\n" : "") +
                (_bridge?.LastError is not null ? "Error: " + _bridge.LastError + "\n" : "") +
                "\nAddresses (give Muse the Tailscale one):\n" +
                string.Join("\n", addresses) +
                "\n\nToken (keep private — it unlocks this PC's bridge):\n" +
                (_bridge?.Token ?? "(no bridge)") +
                "\nToken fingerprint (safe to share): " +
                (_bridge?.ExpectedTokenFingerprint ?? "-") +
                "\n\nLast rejected call: " +
                (_bridge is null || _bridge.AuthFailureCount == 0
                    ? "none since this app started"
                    : _bridge.LastAuthFailureAt + " — reason: " + _bridge.LastAuthFailureCategory +
                      ", received fingerprint: " +
                      (string.IsNullOrEmpty(_bridge.LastAuthFailureReceivedFingerprint)
                          ? "(none)" : _bridge.LastAuthFailureReceivedFingerprint) +
                      " (" + _bridge.AuthFailureCount + " total)") +
                "\nReasons: no_token = no credential arrived; surrogate_received = " +
                "Muse's placeholder was not swapped for the real token on the way; " +
                "token_mismatch = a different token arrived." +
                "\n\nYour Muse reaches the bridge over your private Tailscale network.\n" +
                "Log: " + Path.Combine(_dataDir, "bridge-log.jsonl"),
        };
        var copyToken = new Button { Text = "Copy token", Dock = DockStyle.Bottom, Height = 34 };
        copyToken.Click += (_, _) =>
        {
            if (_bridge is not null) Clipboard.SetText(_bridge.Token);
        };
        var copyAddr = new Button { Text = "Copy Tailscale address", Dock = DockStyle.Bottom, Height = 34 };
        copyAddr.Click += (_, _) =>
        {
            var ts = addresses.FirstOrDefault(a => !a.Contains("127.0.0.1"));
            Clipboard.SetText(ts ?? addresses[0]);
        };
        form.Controls.Add(label);
        form.Controls.Add(copyAddr);
        form.Controls.Add(copyToken);
        form.ShowDialog(this);
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        try
        {
            UnregisterConfiguredHotkeys();
            if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
            _bridge?.Dispose();
        }
        catch { }
        try
        {
            Directory.CreateDirectory(_dataDir);
            var state = new SavedWindow
            {
                Bounds = WindowState == FormWindowState.Normal
                    ? Bounds
                    : RestoreBounds,
                Maximized = WindowState == FormWindowState.Maximized,
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Window memory is a nicety; never block closing over it.
        }
    }

    private void RestoreSavedBounds()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            var state = JsonSerializer.Deserialize<SavedWindow>(
                File.ReadAllText(SettingsPath));
            if (state is null || state.Bounds.Width < 200 || state.Bounds.Height < 200)
            {
                return;
            }

            // Only restore if the saved spot is still on a connected screen.
            foreach (var screen in Screen.AllScreens)
            {
                if (!screen.WorkingArea.IntersectsWith(state.Bounds))
                {
                    continue;
                }

                StartPosition = FormStartPosition.Manual;
                Bounds = state.Bounds;
                if (state.Maximized)
                {
                    WindowState = FormWindowState.Maximized;
                }

                return;
            }
        }
        catch
        {
            // Corrupt settings file: fall back to defaults.
        }
    }

    private sealed class SavedWindow
    {
        public Rectangle Bounds { get; set; }
        public bool Maximized { get; set; }
    }
}
