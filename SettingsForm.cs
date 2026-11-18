using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace MuseDesktop;

/// <summary>
/// The Settings window: every preference in one place, each with a
/// plain one-line explanation of what it does. Changes apply as
/// they are made (MainForm.ApplySettings is the single funnel), so
/// there is no Save button to forget. Opened from the top bar's
/// gear or the tray menu.
///
/// Layout is fully explicit: every container has a fixed size and
/// position, computed once from constant row heights. No auto-size
/// containers and no flow layouts — the first version nested those
/// inside the scroll area and the layout fought itself (CPU spin,
/// scrambled rows). Constants keep that class of bug out.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly MainForm _owner;
    private ChromePalette _pal;
    private bool _loading = true;

    private const int RowHeight = 62;
    private const int SectionHeaderHeight = 30;
    private const int ContentWidth = 500;

    private readonly CheckBox _chkHotkeys = new();
    private readonly TextBox _txtFront = new();
    private readonly TextBox _txtMute = new();
    private readonly Label _bridgeStatus = new();
    private readonly Label _bridgeDetail = new();
    private readonly Button _btnPause = new();
    private readonly CheckBox _chkLogin = new();
    private readonly CheckBox _chkTray = new();
    private readonly CheckBox _chkTop = new();
    private readonly RadioButton _rbDark = new();
    private readonly RadioButton _rbGraphite = new();
    private readonly RadioButton _rbLight = new();
    private readonly Label _status = new();

    public SettingsForm(MainForm owner)
    {
        _owner = owner;
        _pal = Chrome.For(owner.Settings.FrameShade);

        Text = "Muse Settings";
        ClientSize = new Size(540, 700);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = _pal.FormBack;
        ForeColor = _pal.Text;

        BuildLayout();
        LoadValues();
        _loading = false;
    }

    // ---------- Layout (explicit positions; y is a cursor) ----------

    private void BuildLayout()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 66 };
        var title = new Label
        {
            Text = "Settings",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            Location = new Point(20, 10),
            AutoSize = true,
            Tag = "head",
        };
        var sub = new Label
        {
            Text = "Changes apply right away — there is no save button.",
            Location = new Point(21, 42),
            AutoSize = true,
            Tag = "sub",
        };
        header.Controls.Add(title);
        header.Controls.Add(sub);
        Controls.Add(header);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 54 };
        _status.Location = new Point(20, 18);
        _status.AutoSize = true;
        _status.Tag = "sub";
        footer.Controls.Add(_status);
        var reset = MakeButton("Reset to defaults", 150);
        reset.Location = new Point(248, 12);
        reset.Click += (_, _) =>
        {
            _owner.ResetSettings();
            _loading = true;
            LoadValues();
            _loading = false;
            SetStatus("Back to the defaults.");
        };
        footer.Controls.Add(reset);
        var close = MakeButton("Done", 72);
        close.Location = new Point(448, 12);
        close.BackColor = Chrome.AccentBlue;
        close.ForeColor = Color.White;
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Close();
        footer.Controls.Add(close);
        AcceptButton = close;
        Controls.Add(footer);

        // Scroll viewport with one fixed-size content panel inside.
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var content = new Panel { Location = new Point(8, 0), Width = ContentWidth + 8 };
        scroll.Controls.Add(content);
        Controls.Add(scroll);
        scroll.BringToFront();

        var y = 8;

        // --- Voice ---
        Section(content, ref y, "Voice");
        CheckRow(content, ref y, _chkHotkeys, "Global hotkeys",
            "Bring Muse forward or mute from any app, even mid-game.");
        _chkHotkeys.CheckedChanged += (_, _) => Apply(() => _owner.Settings.HotkeysEnabled = _chkHotkeys.Checked);
        TextRow(content, ref y, "Show Muse key", "The key that brings this window to the front.", _txtFront);
        TextRow(content, ref y, "Mute key", "The key that mutes or unmutes a voice call.", _txtMute);
        _txtFront.Leave += (_, _) => CommitHotkey(_txtFront, v => _owner.Settings.BringToFrontHotkey = v, () => _owner.Settings.BringToFrontHotkey);
        _txtMute.Leave += (_, _) => CommitHotkey(_txtMute, v => _owner.Settings.MuteHotkey = v, () => _owner.Settings.MuteHotkey);
        var soundBtn = MakeButton("Sound settings", 130);
        soundBtn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
            catch { SetStatus("Windows Settings would not open."); }
        };
        ControlRow(content, ref y, "Microphone", "Uses the microphone Windows is set to use.", soundBtn);

        // --- Bridge ---
        Section(content, ref y, "Bridge");
        _bridgeStatus.Location = new Point(8, y + 2);
        _bridgeStatus.AutoSize = true;
        _bridgeStatus.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _bridgeStatus.Tag = "head";
        _bridgeDetail.Location = new Point(8, y + 26);
        _bridgeDetail.AutoSize = true;
        _bridgeDetail.Tag = "sub";
        content.Controls.Add(_bridgeStatus);
        content.Controls.Add(_bridgeDetail);
        y += RowHeight;
        var restart = MakeButton("Restart bridge", 124);
        restart.Location = new Point(8, y + 4);
        restart.Click += (_, _) => { _owner.RestartBridge(); RefreshBridge(); SetStatus("Bridge restarted."); };
        content.Controls.Add(restart);
        _btnPause.Text = "Pause bridge";
        StyleButton(_btnPause);
        _btnPause.Size = new Size(118, 30);
        _btnPause.Location = new Point(140, y + 4);
        _btnPause.Click += (_, _) =>
        {
            if (_owner.Bridge is null) return;
            _owner.Bridge.Paused = !_owner.Bridge.Paused;
            _owner.RefreshBridgeStatus();
            RefreshBridge();
        };
        content.Controls.Add(_btnPause);
        var copy = MakeButton("Copy token", 108);
        copy.Location = new Point(266, y + 4);
        copy.Click += (_, _) =>
        {
            if (_owner.Bridge is not null)
            {
                Clipboard.SetText(_owner.Bridge.Token);
                SetStatus("Token copied — keep it private.");
            }
        };
        content.Controls.Add(copy);
        var log = MakeButton("Open log", 94);
        log.Location = new Point(382, y + 4);
        log.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(_owner.BridgeLogPath) { UseShellExecute = true }); }
            catch { SetStatus("No bridge log yet."); }
        };
        content.Controls.Add(log);
        y += RowHeight;

        // --- Window ---
        Section(content, ref y, "Window");
        CheckRow(content, ref y, _chkLogin, "Start when I sign in",
            "Launch Muse automatically when you sign in to Windows.");
        _chkLogin.CheckedChanged += (_, _) => Apply(() => _owner.Settings.StartOnLogin = _chkLogin.Checked);
        CheckRow(content, ref y, _chkTray, "Minimize to the tray",
            "Minimizing hides Muse in the system tray instead of the taskbar, so a call keeps running.");
        _chkTray.CheckedChanged += (_, _) => Apply(() => _owner.Settings.MinimizeToTray = _chkTray.Checked);
        CheckRow(content, ref y, _chkTop, "Always on top",
            "Keep this window above other windows. A fullscreen game still takes over.");
        _chkTop.CheckedChanged += (_, _) => Apply(() => _owner.Settings.AlwaysOnTop = _chkTop.Checked);

        // --- Appearance ---
        Section(content, ref y, "Appearance");
        var shades = new Panel { Size = new Size(250, 30) };
        var shadeX = 0;
        foreach (var (rb, name) in new[] { (_rbDark, "Dark"), (_rbGraphite, "Graphite"), (_rbLight, "Light") })
        {
            rb.Text = name;
            rb.AutoSize = true;
            rb.Location = new Point(shadeX, 5);
            shadeX += name == "Dark" ? 74 : 96;
            rb.CheckedChanged += (_, _) =>
            {
                if (rb.Checked)
                {
                    Apply(() => _owner.Settings.FrameShade = name);
                    _pal = Chrome.For(name);
                    Recolor(this);
                }
            };
            shades.Controls.Add(rb);
        }
        ControlRow(content, ref y, "Frame shade",
            "Dresses the window frame and this page. The Muse page inside never changes.", shades);

        // --- About ---
        Section(content, ref y, "About");
        var version = new Label
        {
            Text = "Version " + Application.ProductVersion,
            AutoSize = true,
            Tag = "sub",
        };
        ControlRow(content, ref y, "Muse for Windows", "This PC's window for Muse, with the Muse Bridge.", version);
        var releases = MakeButton("Releases page", 130);
        releases.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/treshepard/muse-desktop-windows/releases") { UseShellExecute = true }); }
            catch { SetStatus("The browser would not open."); }
        };
        ControlRow(content, ref y, "Updates", "New versions are published on the project's releases page.", releases);

        content.Height = y + 10;
        Recolor(this);
    }

    private void Section(Panel content, ref int y, string name)
    {
        var head = new Label
        {
            Text = name,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            Location = new Point(8, y),
            AutoSize = true,
            Tag = "head",
        };
        content.Controls.Add(head);
        y += SectionHeaderHeight;
    }

    private void CheckRow(Panel content, ref int y, CheckBox box, string label, string desc)
    {
        box.Text = label;
        box.AutoSize = true;
        box.Location = new Point(8, y + 2);
        box.Tag = "head";
        var d = new Label
        {
            Text = desc,
            Location = new Point(29, y + 27),
            Size = new Size(ContentWidth - 29, 33),
            Tag = "sub",
        };
        content.Controls.Add(box);
        content.Controls.Add(d);
        y += RowHeight;
    }

    private void TextRow(Panel content, ref int y, string label, string desc, TextBox box)
    {
        box.Size = new Size(130, 27);
        ControlRow(content, ref y, label, desc, box);
    }

    // Label and plain description on the left, the control pinned
    // to the right edge of the row.
    private void ControlRow(Panel content, ref int y, string label, string desc, Control control)
    {
        var l = new Label { Text = label, Location = new Point(8, y + 3), AutoSize = true, Tag = "head" };
        var d = new Label
        {
            Text = desc,
            Location = new Point(8, y + 25),
            Size = new Size(330, 34),
            Tag = "sub",
        };
        content.Controls.Add(l);
        content.Controls.Add(d);
        control.Location = new Point(8 + ContentWidth - control.Width, y + 4);
        content.Controls.Add(control);
        y += RowHeight;
    }

    private Button MakeButton(string text, int width)
    {
        var btn = new Button { Text = text, Size = new Size(width, 30) };
        StyleButton(btn);
        return btn;
    }

    private void StyleButton(Button btn)
    {
        btn.FlatStyle = FlatStyle.Flat;
        btn.BackColor = _pal.Field;
        btn.ForeColor = _pal.Text;
        btn.FlatAppearance.BorderColor = _pal.Border;
    }

    // ---------- Behavior ----------

    private void LoadValues()
    {
        var s = _owner.Settings;
        _chkHotkeys.Checked = s.HotkeysEnabled;
        _txtFront.Text = s.BringToFrontHotkey;
        _txtMute.Text = s.MuteHotkey;
        _chkLogin.Checked = s.StartOnLogin;
        _chkTray.Checked = s.MinimizeToTray;
        _chkTop.Checked = s.AlwaysOnTop;
        _rbDark.Checked = s.FrameShade == "Dark";
        _rbGraphite.Checked = s.FrameShade == "Graphite";
        _rbLight.Checked = s.FrameShade == "Light";
        RefreshBridge();
    }

    private void RefreshBridge()
    {
        var bridge = _owner.Bridge;
        if (bridge is null)
        {
            _bridgeStatus.Text = "Not running";
            _bridgeDetail.Text = _owner.BridgeError is not null
                ? "It failed to start: " + _owner.BridgeError
                : "The bridge is off. Restart it to let Muse reach this PC.";
        }
        else if (bridge.Paused)
        {
            _bridgeStatus.Text = "Paused";
            _bridgeDetail.Text = "Running on this PC only (127.0.0.1:" + BridgeServer.Port + "), but paused — calls are refused.";
        }
        else if (bridge.Running)
        {
            _bridgeStatus.Text = "Running, this PC only";
            _bridgeDetail.Text = "Listening on 127.0.0.1:" + BridgeServer.Port + ". Your Muse reaches it over Tailscale.";
        }
        else
        {
            _bridgeStatus.Text = "Not running";
            _bridgeDetail.Text = "The bridge stopped. Restart it to let Muse reach this PC.";
        }
        _btnPause.Text = bridge?.Paused == true ? "Resume bridge" : "Pause bridge";
        _btnPause.Enabled = bridge is not null;
    }

    private void Apply(Action change)
    {
        if (_loading)
        {
            return;
        }
        change();
        _owner.ApplySettings();
    }

    private void CommitHotkey(TextBox box, Action<string> set, Func<string> get)
    {
        if (_loading)
        {
            return;
        }
        var value = box.Text.Trim();
        if (GlobalHotkey.TryParse(value, out _, out _))
        {
            set(value);
            _owner.ApplySettings();
            SetStatus("");
        }
        else
        {
            box.Text = get();
            SetStatus("That key combo is not valid — try F9, or Ctrl+Alt+M.");
        }
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
    }

    private void Recolor(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case Label l:
                    l.ForeColor = (l.Tag as string) == "sub" ? _pal.SubText : _pal.Text;
                    break;
                case CheckBox cb:
                    cb.ForeColor = _pal.Text;
                    break;
                case RadioButton rb:
                    rb.ForeColor = _pal.Text;
                    break;
                case TextBox tb:
                    tb.BackColor = _pal.Field;
                    tb.ForeColor = _pal.Text;
                    break;
                case Button b when b.Text != "Done":
                    b.BackColor = _pal.Field;
                    b.ForeColor = _pal.Text;
                    b.FlatAppearance.BorderColor = _pal.Border;
                    break;
            }
            Recolor(c);
        }
        BackColor = _pal.FormBack;
        ForeColor = _pal.Text;
    }
}
