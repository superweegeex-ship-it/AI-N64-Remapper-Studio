using System.Text.Json;

namespace N64Studio;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args is ["--self-test", var test]) { Diagnostics.Run(test, false); return; }
        if (args is ["--probe", var probe]) { Diagnostics.Run(probe, true); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new Studio(args.Contains("--demo")));
    }
}

sealed class Studio : Form
{
    internal static readonly Color Background = Color.FromArgb(17, 23, 35);
    internal static readonly Color Surface = Color.FromArgb(26, 35, 52);
    internal static readonly Color Ink = Color.FromArgb(233, 240, 251);
    internal static readonly Color Muted = Color.FromArgb(172, 190, 213);
    internal static readonly Color Accent = Color.FromArgb(91, 230, 187);
    readonly Label connection = TextLine("Offline  •  Connect a Remapper or load a profile", Muted);
    readonly Label status = TextLine("Edit a local draft, apply it to the device, then save when ready.", Muted);
    readonly Label calibration = TextLine("Connect a Remapper to calibrate.", Ink);
    readonly Label live = TextLine("Waiting for live input", Muted);
    readonly Label buttons = TextLine("", Accent);
    readonly StickView view = new() { Dock = DockStyle.Fill };
    readonly DataGridView notchGrid = Grid();
    readonly DataGridView mappingGrid = Grid();
    readonly TextBox threshold = new() { Width = 93 };
    readonly TextBox cutoff = new() { Width = 93 };
    readonly CheckBox gate = new() { Text = "Limit output to gate", AutoSize = true };
    readonly Button connect = Action("Connect", true);
    readonly Button demo = Action("Demo");
    readonly Button read = Action("Read device");
    readonly Button apply = Action("Apply + verify", true);
    readonly Button save = Action("Save to device");
    readonly Button start = Action("Start calibration");
    readonly Button next = Action("Capture step", true);
    readonly Button previous = Action("Previous step");
    readonly Button reset = Action("Reset to defaults");
    readonly Button boot = Action("Enter bootloader");
    readonly TableLayoutPanel root = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    readonly JsonSerializerOptions json = new() { WriteIndented = true, RespectRequiredConstructorParameters = true };
    Profile draft = Profile.Defaults();
    Snapshot? snapshot;
    Device? device;
    bool busy, simulated, pending, loading;
    bool dirty;
    static readonly string[] MappingNames = ["Default", ..Profile.Buttons[..14], ..Enumerable.Range(15,18).Select(i => $"Output bit {i}"), "Disabled"];
    static string BindingLabel(int code) => MappingNames[code == 255 ? 33 : code];

    public Studio(bool launchDemo)
    {
        Text = "N64 Remapper Studio";
        Font = new Font("Segoe UI", 10);
        BackColor = Background; ForeColor = Ink;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1190, 760);
        MinimumSize = new Size(980, 690);
        root.Dock = DockStyle.Fill;
        root.Padding = new Padding(22, 12, 22, 12);
        root.RowCount = 5; root.ColumnCount = 1;
        foreach (int height in new[] { 77, 50, -1, 48, 44 })
            root.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        Controls.Add(root);

        var heading = new Panel { Dock = DockStyle.Fill };
        heading.Controls.Add(new Label { Text = "N64 Remapper Studio", ForeColor = Ink, Font = new Font("Segoe UI", 24, FontStyle.Bold), AutoSize = true, Location = new Point(0, 0) });
        connection.Location = new Point(3, 49);
        heading.Controls.Add(connection);
        root.Controls.Add(heading, 0, 0);

        Button load = Action("Open profile…");
        Button export = Action("Export profile…");
        root.Controls.Add(Row(connect, demo, load, export), 0, 1);

        var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        root.Controls.Add(workspace, 0, 2);
        var monitor = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(14), Margin = new Padding(0,0,14,0), ColumnCount = 1, RowCount = 4 };
        monitor.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        monitor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        monitor.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        monitor.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
        monitor.Controls.Add(TextLine("LIVE STICK     ● Raw input     ○ Processed output", Accent), 0, 0);
        monitor.Controls.Add(view, 0, 1);
        monitor.Controls.Add(live, 0, 2);
        buttons.AutoSize = false; buttons.Dock = DockStyle.Fill;
        monitor.Controls.Add(buttons, 0, 3);
        workspace.Controls.Add(monitor, 0, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14,6) };
        workspace.Controls.Add(tabs, 1, 0);
        TabPage stick = Tab(tabs, "Stick tuning");
        var tuning = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        tuning.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        tuning.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tuning.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        tuning.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        stick.Controls.Add(tuning);
        tuning.Controls.Add(TextLine("Output notches  /  signed coordinates and angular deadzone", Muted), 0, 0);
        notchGrid.Columns.Add("Direction", "Direction");
        notchGrid.Columns[0].ReadOnly = true;
        notchGrid.Columns.Add("X", "X");
        notchGrid.Columns.Add("Y", "Y");
        notchGrid.Columns.Add("Angle", "Deadzone (°)");
        tuning.Controls.Add(notchGrid, 0, 1);
        tuning.Controls.Add(Row(TextLine("Threshold (%)", Ink), threshold, TextLine("Filter (Hz)", Ink), cutoff), 0, 2);
        tuning.Controls.Add(Row(gate), 0, 3);

        TabPage mapping = Tab(tabs, "Button mapping");
        var map = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        map.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        map.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mapping.Controls.Add(map);
        map.Controls.Add(TextLine("Choose the N64 output for each controller or GPIO input.", Muted), 0, 0);
        mappingGrid.Columns.Add("Input", "Input");
        mappingGrid.Columns[0].ReadOnly = true;
        mappingGrid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "N64 output", DataSource = MappingNames, FlatStyle = FlatStyle.Flat });
        map.Controls.Add(mappingGrid, 0, 1);

        TabPage setup = Tab(tabs, "Calibration & device");
        var setupRows = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        setup.Controls.Add(setupRows);
        setupRows.Controls.Add(TextLine("GUIDED STICK CALIBRATION", Accent));
        setupRows.Controls.Add(TextLine("Follow the target on the live plot. Hold each position until it advances.", Muted));
        setupRows.Controls.Add(calibration);
        setupRows.Controls.Add(Row(start, previous, next));
        setupRows.Controls.Add(TextLine("DEVICE MAINTENANCE", Accent));
        setupRows.Controls.Add(TextLine("Reset clears the device's current calibration and settings in memory.", Muted));
        setupRows.Controls.Add(Row(reset, boot));

        root.Controls.Add(Row(read, apply, save), 0, 3);
        status.Dock = DockStyle.Fill; status.AutoSize = false;
        root.Controls.Add(status, 0, 4);
        connect.Click += async (_, _) => { if (device is not null) Disconnect(); else await Connect(false); };
        demo.Click += async (_, _) => await Connect(true);
        read.Click += async (_, _) => { if (DiscardDraft()) await Run(() => device!.Read(), s => Accept(s, "Device settings loaded.")); };
        apply.Click += async (_, _) =>
        {
            if (!TryDraft(out var profile)) return;
            await Run(() => device!.Apply(profile, snapshot!), s =>
            {
                bool changed = !s.Profile.Matches(snapshot!.Profile);
                pending |= changed;
                Accept(s, pending ? "Readback verified. Save to device to keep the current settings after unplugging." :
                    "Readback verified. The draft already matches the device.");
            });
        };
        save.Click += async (_, _) =>
        {
            if (!TryDraft(out var profile) || !profile.Matches(snapshot!.Profile))
            {
                Notice("Apply the draft before saving.");
                return;
            }
            if (!Confirm("Write the device's current settings and calibration to flash?")) return;
            await Run(() => { device!.Save(snapshot!); return true; }, _ =>
            {
                pending = false;
                Notice("Save requested and settings read back. Power-cycle to confirm flash persistence.");
            });
        };
        start.Click += async (_, _) => await Calibrate(1);
        next.Click += async (_, _) => await Calibrate(2);
        previous.Click += async (_, _) => await Calibrate(3);
        reset.Click += async (_, _) =>
        {
            if (!Confirm("Reset the device settings and calibration in memory and replace the local draft?")) return;
            await Run(() => device!.Reset(), s => { pending = true; Accept(s, "Defaults loaded in device memory. Save separately to keep them."); });
        };
        boot.Click += async (_, _) =>
        {
            if (!Confirm("Restart the Remapper into USB bootloader mode? Unsaved device changes will be lost.")) return;
            await Run(() => { device!.Bootloader(); return true; }, _ => Disconnect(false, "Bootloader requested. Reconnect after the device restarts."));
        };
        load.Click += (_, _) => LoadProfile();
        export.Click += (_, _) => ExportProfile();
        notchGrid.CellValueChanged += (_, _) => Edited();
        mappingGrid.CellValueChanged += (_, _) => Edited();
        mappingGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (mappingGrid.IsCurrentCellDirty) mappingGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        threshold.TextChanged += (_, _) => Edited();
        cutoff.TextChanged += (_, _) => Edited();
        gate.CheckedChanged += (_, _) => Edited();
        timer.Tick += (_, _) => RefreshLive();
        timer.Start();
        ShowDraft(draft);
        RefreshActions();
        Shown += async (_, _) => { if (launchDemo) await Connect(true); };
        FormClosing += (_, e) =>
        {
            if (busy || ((pending || dirty) && !Confirm("Close with unsaved device changes or a local draft?"))) e.Cancel = true;
            else { timer.Dispose(); device?.Dispose(); }
        };
    }

    static Label TextLine(string text, Color color) => new() { Text = text, ForeColor = color, AutoSize = true, Margin = new Padding(3, 7, 6, 4) };
    static Button Action(string label, bool primary = false)
    {
        var b = new Button { Text = label, AutoSize = true, MinimumSize = new Size(0, 34), Padding = new Padding(9,0,9,0),
            FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Surface, ForeColor = primary ? Background : Ink, Margin = new Padding(0,3,8,3) };
        b.FlatAppearance.BorderColor = Color.FromArgb(68, 88, 111);
        b.EnabledChanged += (_, _) => b.BackColor = b.Enabled && primary ? Accent : Surface;
        return b;
    }
    static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }
    static TabPage Tab(TabControl tabs, string name)
    {
        var page = new TabPage(name) { BackColor = Surface, ForeColor = Ink, Padding = new Padding(12) };
        tabs.TabPages.Add(page);
        return page;
    }
    static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowTemplate = { Height = 29 },
        BackgroundColor = Surface, BorderStyle = BorderStyle.None, GridColor = Color.FromArgb(60, 76, 99),
        EnableHeadersVisualStyles = false,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Background, ForeColor = Ink },
        DefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = Ink, SelectionBackColor = Color.FromArgb(44,74,97), SelectionForeColor = Ink },
        AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(32,43,62) }
    };

    void Edited()
    {
        if (!loading)
        {
            dirty = true;
            Notice("Local draft edited. Apply + verify to send changes to the device.");
        }
    }

    void ShowDraft(Profile profile)
    {
        loading = true;
        draft = profile;
        notchGrid.Rows.Clear();
        mappingGrid.Rows.Clear();
        for (int i = 0; i < 8; i++)
        {
            Notch n = profile.Notches[i];
            notchGrid.Rows.Add(Profile.Directions[i], n.X, n.Y, (n.Radians == MathF.PI ? 180.0 : n.Radians * 180.0 / Math.PI).ToString("G9"));
        }
        for (int i = 0; i < Profile.Buttons.Length; i++)
            mappingGrid.Rows.Add(Profile.Buttons[i], BindingLabel(profile.Bindings[i]));
        threshold.Text = (double.Parse(profile.Threshold.ToString("R")) * 100.0).ToString("G9");
        cutoff.Text = profile.CutoffHz.ToString("R");
        gate.Checked = profile.Gate;
        loading = false;
        dirty = false;
    }

    Profile CaptureDraft()
    {
        notchGrid.EndEdit();
        mappingGrid.EndEdit();
        var notches = new Notch[8];
        for (int i = 0; i < 8; i++)
        {
            var c = notchGrid.Rows[i].Cells;
            if (!int.TryParse(c[1].Value?.ToString(), out int x) || !int.TryParse(c[2].Value?.ToString(), out int y) ||
                !double.TryParse(c[3].Value?.ToString(), out double angle) || !double.IsFinite(angle) || angle < 0 || angle > 180)
                throw new InvalidDataException($"Check the X, Y and deadzone fields for {Profile.Directions[i]}.");
            notches[i] = new(x,y,(float)(angle*Math.PI/180.0));
        }
        if (!double.TryParse(threshold.Text, out double percent) || !float.TryParse(cutoff.Text, out float hz))
            throw new InvalidDataException("Enter a numeric threshold and filter cutoff.");
        int[] bindings = (int[])draft.Bindings.Clone();
        for (int i = 0; i < Profile.Buttons.Length; i++)
        {
            int selected = Array.IndexOf(MappingNames, mappingGrid.Rows[i].Cells[1].Value?.ToString());
            bindings[i] = selected == 33 ? 255 : selected;
        }
        var result = new Profile(1, notches, (float)(percent/100), hz, gate.Checked, bindings);
        result.Validate();
        return result;
    }

    bool TryDraft(out Profile profile)
    {
        profile = draft;
        try { profile = CaptureDraft(); return true; }
        catch (Exception ex) { Error(ex); return false; }
    }

    void LoadProfile()
    {
        if (!DiscardDraft()) return;
        using var dialog = new OpenFileDialog { Filter = "N64 profile (*.json)|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var file = File.OpenRead(dialog.FileName);
            if (file.Length > 128*1024) throw new InvalidDataException("Profile file is too large.");
            var profile = JsonSerializer.Deserialize<Profile>(file, json) ?? throw new InvalidDataException("Empty profile.");
            profile.Validate();
            ShowDraft(profile);
            dirty = true;
            Notice($"Loaded {Path.GetFileName(dialog.FileName)} into the local draft.");
        }
        catch (Exception ex) { Error(ex); }
    }

    void ExportProfile()
    {
        if (!TryDraft(out var profile)) return;
        using var dialog = new SaveFileDialog { Filter = "N64 profile (*.json)|*.json", FileName = "N64 profile.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(profile, json));
            Notice($"Exported {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) { Error(ex); }
    }

    async Task Connect(bool useDemo)
    {
        if (device is not null) return;
        Profile? keep = null;
        if (dirty)
        {
            if (!TryDraft(out var current)) return;
            keep = current;
        }
        await Run(() =>
        {
            var link = new Device(useDemo ? new Demo() : new WindowsUsb());
            try { return (link, link.Version(), link.Read()); }
            catch { link.Dispose(); throw; }
        }, result =>
        {
            (device, string version, Snapshot state) = result;
            simulated = useDemo;
            connection.Text = useDemo ? "SIMULATION  •  No hardware writes" : $"CONNECTED  •  N64 Remapper  •  {version}";
            Accept(state, useDemo ? "Demo ready. Try editing, applying or calibrating." : "Device settings loaded. The editor is a local draft.");
            if (keep is not null)
            {
                ShowDraft(keep);
                dirty = true;
                Notice("Connected. Your local draft is still open. Read device to replace it.");
            }
        });
    }

    void Disconnect(bool confirm = true, string message = "Disconnected. The local draft is still open.")
    {
        if (confirm && pending && !Confirm("Disconnect with changes that may still need saving?")) return;
        device?.Dispose();
        device = null; snapshot = null; pending = false; simulated = false;
        connection.Text = "Offline  •  Connect a Remapper or load a profile";
        calibration.Text = "Connect a Remapper to calibrate.";
        Notice(message);
        RefreshActions();
    }

    async Task Calibrate(byte command)
    {
        if (!DiscardDraft()) return;
        await Run(() => device!.Calibrate(command, snapshot!.Step), s =>
        {
            pending = true;
            Accept(s, s.Step == 0 ? "Calibration complete in device memory. Save separately to keep it." : "Step verified by firmware.");
        });
    }

    void Accept(Snapshot state, string message)
    {
        snapshot = state;
        ShowDraft(state.Profile);
        calibration.Text = state.Step == 0 ? (state.Calibrated ? "Calibrated" : "Calibration required") :
            $"Step {state.Step}/16  •  " + (state.Step % 2 == 1 ? "Release the stick to center" : $"Hold {Profile.Directions[(state.Step-1)/2]}");
        Notice(message);
    }

    async Task Run<T>(Func<T> work, Action<T> success)
    {
        busy = true;
        root.Enabled = false;
        Notice("Communicating with device…");
        try { success(await Task.Run(work)); }
        catch (Exception ex)
        {
            if (device is not null)
            {
                snapshot = null;
                pending = true;
                calibration.Text = "Read the device to refresh calibration status.";
            }
            Error(ex);
        }
        finally { busy = false; root.Enabled = true; RefreshActions(); }
    }

    void RefreshActions()
    {
        bool attached = device is not null;
        bool idle = snapshot?.Step == 0;
        connect.Text = attached ? "Disconnect" : "Connect";
        demo.Enabled = !attached;
        read.Enabled = attached;
        apply.Enabled = save.Enabled = start.Enabled = reset.Enabled = attached && idle;
        boot.Enabled = attached && !simulated && idle;
        next.Enabled = snapshot?.Step > 0;
        previous.Enabled = snapshot?.Step > 1;
        bool editing = snapshot?.Step is null or 0;
        notchGrid.Enabled = mappingGrid.Enabled = threshold.Enabled = cutoff.Enabled = gate.Enabled = editing;
    }

    void RefreshLive()
    {
        InputFrame? frame = device?.Input;
        if (frame is not null && Environment.TickCount64 - frame.Time > 1000) frame = null;
        int step = snapshot?.Step ?? 0;
        view.UpdateFrame(frame, step);
        live.Text = frame is null ? (device is null ? "Waiting for live input" : "No current HID reports") :
            $"Output  {frame.X,4}, {frame.Y,4}     Raw  {frame.RawX:F3}, {frame.RawY:F3}";
        buttons.Text = frame is null ? "" : string.Join("  ·  ", Profile.Buttons.Where((_, i) => (frame.Buttons & (1u << i)) != 0));
    }

    bool DiscardDraft() => !dirty || Confirm("Replace the current local draft?");
    bool Confirm(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    void Notice(string text) => status.Text = text;
    void Error(Exception ex)
    {
        Notice(ex.Message);
        MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}

sealed class StickView : Control
{
    InputFrame? frame;
    int step;
    readonly Notch[] shape = Profile.Defaults().Notches;
    public StickView() { DoubleBuffered = true; }
    public void UpdateFrame(InputFrame? next, int target)
    {
        if (frame == next && step == target) return;
        frame = next; step = target; Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float r = Math.Min(Width, Height)/2f - 12;
        if (r < 5) return;
        PointF Point(float x, float y) => new(Width/2f + Math.Clamp(x,-1,1)*r, Height/2f - Math.Clamp(y,-1,1)*r);
        using var grid = new Pen(Color.FromArgb(55, 74, 98));
        using var outline = new Pen(Studio.Muted, 2);
        using var raw = new SolidBrush(Studio.Accent);
        using var target = new Pen(Color.FromArgb(255, 191, 100), 3);
        PointF origin = Point(-1,1);
        using var plot = new SolidBrush(Studio.Background);
        g.FillRectangle(plot, origin.X, origin.Y, 2*r, 2*r);
        for (int i = -4; i <= 4; i++)
        {
            float t = i/4f;
            g.DrawLine(grid, Point(t,-1), Point(t,1));
            g.DrawLine(grid, Point(-1,t), Point(1,t));
        }
        g.DrawPolygon(outline, shape.Select(n => Point(n.X/127f,n.Y/127f)).ToArray());
        if (step is >= 1 and <= 16)
        {
            Notch n = step % 2 == 1 ? new(0,0,0) : shape[(step-1)/2];
            PointF p = Point(n.X/127f,n.Y/127f);
            g.DrawEllipse(target, p.X-11,p.Y-11,22,22);
        }
        if (frame is null) return;
        PointF output = Point(frame.X/127f,frame.Y/127f);
        PointF input = Point(frame.RawX,frame.RawY);
        g.DrawEllipse(outline, output.X-7,output.Y-7,14,14);
        g.FillEllipse(raw, input.X-5,input.Y-5,10,10);
    }
}


