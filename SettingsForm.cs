using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MuseDesktop;

/// <summary>
/// The Settings window: every preference in one place, each with a
/// plain one-line explanation of what it does. Changes apply as
/// they are made (MainForm.ApplySettings is the single funnel), so
/// there is no Save button to forget. Opened from the top bar's
/// gear or the tray menu.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly MainForm _owner;
    private ChromePalette _pal;
    private bool _loading = true;

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
        ClientSize = new Size(540, 668);
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

    // ---------- Layout ----------

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
        var reset = MakeButton("Reset to defaults");
        reset.Location = new Point(268, 12);
        reset.Click += (_, _) =>
        {
            _owner.ResetSettings();
            _loading = true;
            LoadValues();
            _loading = false;
            SetStatus("Back to the defaults.");
        };
        footer.Controls.Add(reset);
        var close = MakeButton("Done");
        close.Location = new Point(428, 12);
        close.BackColor = Chrome.AccentBlue;
        close.ForeColor = Color.White;
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Close();
        footer.Controls.Add(close);
        AcceptButton = close;
        Controls.Add(footer);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Width = 508,
            Location = new Point(16, 0),
        };
        scroll.Controls.Add(flow);
        Controls.Add(scroll);
        scroll.BringToFront();

        // --- Voice ---
        var voice = Section(flow, "Voice");
        CheckRow(voice, _chkHotkeys, "Global hotkeys",
            "Bring Muse forward or mute from any app, even mid-game.");
        _chkHotkeys.CheckedChanged += (_, _) => Apply(() => _owner.Settings.HotkeysEnabled = _chkHotkeys.Checked);
        Row(voice, "Show Muse key", "The key that brings this window to the front.", _txtFront);
        Row(voice, "Mute key", "The key that mutes or unmutes a voice call.", _txtMute);
        _txtFront.Leave += (_, _) => CommitHotkey(_txtFront, v => _owner.Settings.BringToFrontHotkey = v, () => _owner.Settings.BringToFrontHotkey);
        _txtMute.Leave += (_, _) => CommitHotkey(_txtMute, v => _owner.Settings.MuteHotkey = v, () => _owner.Settings.MuteHotkey);
        var soundBtn = MakeButton("Sound settings");
        soundBtn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
            catch { SetStatus("Windows Settings would not open."); }
        };
        Row(voice, "Microphone", "Muse uses the microphone Windows is set to use.", soundBtn);

        // --- Bridge ---
        var bridge = Section(flow, "Bridge");
        var statusRow = Row(bridge, null, null, null);
        _bridgeStatus.Location = new Point(0, 2);
        _bridgeStatus.AutoSize = true;
        _bridgeStatus.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _bridgeStatus.Tag = "head";
        _bridgeDetail.Location = new Point(0, 26);
        _bridgeDetail.AutoSize = true;
        _bridgeDetail.Tag = "sub";
        statusRow.Controls.Add(_bridgeStatus);
        statusRow.Controls.Add(_bridgeDetail);
        var bridgeBtns = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
        };
        var restart = MakeButton("Restart bridge");
        restart.Click += (_, _) => { _owner.RestartBridge(); RefreshBridge(); SetStatus("Bridge restarted."); };
        _btnPause.Text = "Pause bridge";
        StyleButton(_btnPause);
        _btnPause.AutoSize = true;
        _btnPause.Click += (_, _) =>
        {
            if (_owner.Bridge is null) return;
            _owner.Bridge.Paused = !_owner.Bridge.Paused;
            _owner.RefreshBridgeStatus();
            RefreshBridge();
        };
        var copy = MakeButton("Copy token");
        copy.Click += (_, _) =>
        {
            if (_owner.Bridge is not null)
            {
                Clipboard.SetText(_owner.Bridge.Token);
                SetStatus("Token copied — keep it private.");
            }
        };
        var log = MakeButton("Open log");
        log.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(_owner.BridgeLogPath) { UseShellExecute = true }); }
            catch { SetStatus("No bridge log yet."); }
        };
        bridgeBtns.Controls.Add(restart);
        bridgeBtns.Controls.Add(_btnPause);
        bridgeBtns.Controls.Add(copy);
        bridgeBtns.Controls.Add(log);
        var btnRow = Row(bridge, null, null, null);
        btnRow.Controls.Add(bridgeBtns);

        // --- Window ---
        var window = Section(flow, "Window");
        CheckRow(window, _chkLogin, "Start when I sign in",
            "Launch Muse automatically when you sign in to Windows.");
        _chkLogin.CheckedChanged += (_, _) => Apply(() => _owner.Settings.StartOnLogin = _chkLogin.Checked);
        CheckRow(window, _chkTray, "Minimize to the tray",
            "Minimizing hides Muse in the system tray instead of the taskbar, so a call keeps running.");
        _chkTray.CheckedChanged += (_, _) => Apply(() => _owner.Settings.MinimizeToTray = _chkTray.Checked);
        CheckRow(window, _chkTop, "Always on top",
            "Keep this window above other windows. A fullscreen game still takes over.");
        _chkTop.CheckedChanged += (_, _) => Apply(() => _owner.Settings.AlwaysOnTop = _chkTop.Checked);

        // --- Appearance ---
        var appearance = Section(flow, "Appearance");
        var shades = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        foreach (var (rb, name) in new[] { (_rbDark, "Dark"), (_rbGraphite, "Graphite"), (_rbLight, "Light") })
        {
            rb.Text = name;
            rb.AutoSize = true;
            rb.Margin = new Padding(0, 4, 18, 0);
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
        Row(appearance, "Frame shade",
            "Dresses the window frame and this page. The Muse page inside never changes.", shades);

        // --- About ---
        var about = Section(flow, "About");
        var version = new Label
        {
            Text = "Version " + Application.ProductVersion,
            AutoSize = true,
            Tag = "sub",
        };
        Row(about, "Muse for Windows", "This PC's window for Muse, with the Muse Bridge.", version);
        var releases = MakeButton("Releases page");
        releases.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/treshepard/muse-desktop-windows/releases") { UseShellExecute = true }); }
            catch { SetStatus("The browser would not open."); }
        };
        Row(about, "Updates", "New versions are published on the project's releases page.", releases);

        Recolor(this);
    }

    private Panel Section(FlowLayoutPanel parent, string name)
    {
        var panel = new Panel { Width = 500, AutoSize = true, Margin = new Padding(0, 6, 0, 10) };
        var head = new Label
        {
            Text = name,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            Location = new Point(4, 0),
            AutoSize = true,
            Tag = "head",
        };
        panel.Controls.Add(head);
        var rows = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Width = 500,
            Location = new Point(0, 30),
        };
        rows.Name = "rows";
        panel.Controls.Add(rows);
        parent.Controls.Add(panel);
        return rows;
    }

    // A labeled row: label and plain description on the left, the
    // control on the right. Nulls make an empty row to host custom
    // content in.
    private Panel Row(Control parent, string? label, string? desc, Control? control)
    {
        var row = new Panel { Width = 492, Height = 56, Margin = new Padding(4, 0, 0, 2) };
        if (label is not null)
        {
            var l = new Label { Text = label, Location = new Point(0, 3), AutoSize = true, Tag = "head" };
            row.Controls.Add(l);
        }
        if (desc is not null)
        {
            var d = new Label
            {
                Text = desc,
                Location = new Point(0, 24),
                Size = new Size(320, 28),
                Tag = "sub",
            };
            row.Controls.Add(d);
        }
        if (control is not null)
        {
            control.Location = new Point(492 - control.Width - 4, 6);
            if (control is TextBox)
            {
                control.Size = new Size(130, 27);
                control.Location = new Point(492 - 134, 4);
            }
            row.Controls.Add(control);
        }
        parent.Controls.Add(row);
        return row;
    }

    private void CheckRow(Control parent, CheckBox box, string label, string desc)
    {
        var row = new Panel { Width = 492, Height = 56, Margin = new Padding(4, 0, 0, 2) };
        box.Text = label;
        box.AutoSize = true;
        box.Location = new Point(0, 3);
        box.Tag = "head";
        var d = new Label
        {
            Text = desc,
            Location = new Point(21, 26),
            Size = new Size(460, 28),
            Tag = "sub",
        };
        row.Controls.Add(box);
        row.Controls.Add(d);
        parent.Controls.Add(row);
    }

    private Button MakeButton(string text)
    {
        var btn = new Button { Text = text, AutoSize = true, Height = 30 };
        StyleButton(btn);
        return btn;
    }

    private void StyleButton(Button btn)
    {
        btn.FlatStyle = FlatStyle.Flat;
        btn.BackColor = _pal.Field;
        btn.ForeColor = _pal.Text;
        btn.FlatAppearance.BorderColor = _pal.Border;
        btn.Padding = new Padding(8, 0, 8, 0);
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
