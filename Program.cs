using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GF2Recorder;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed class ActionStep
{
    public string Type { get; set; } = "Tap";
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
    public int DurationMs { get; set; }
    public int WheelDelta { get; set; }
    public int WaitBeforeMs { get; set; }
    public string? TemplateFile { get; set; }
    public int MatchX { get; set; }
    public int MatchY { get; set; }
    public int MatchWidth { get; set; }
    public int MatchHeight { get; set; }
    public int TimeoutMs { get; set; } = 30000;
    public int CheckIntervalMs { get; set; } = 500;
    public double MatchThreshold { get; set; } = 0.82;
    public string MatchMode { get; set; } = "Standard";
    public bool RetryTrigger { get; set; }
    public int RetryX { get; set; }
    public int RetryY { get; set; }
    public int RetryIntervalMs { get; set; } = 3000;
    public string? AlternateTemplateFile { get; set; }
    public int CorrectX { get; set; }
    public int CorrectY { get; set; }
    public string Note { get; set; } = "";
    public override string ToString() => Type switch
    {
        "Tap" => $"{WaitBeforeMs} ms　点击 ({X1}, {Y1})　普通点击　{Note}",
        "SmartTap" => $"{WaitBeforeMs} ms　点击 ({X1}, {Y1})　智能确认·{(MatchMode == "BrightUI" ? "动态白字" : "标准图像")}·" +
                      (RetryTrigger ? $"每 {RetryIntervalMs / 1000.0:0.#} 秒重试上一步" : "只等待"),
        "StateCorrection" => $"{WaitBeforeMs} ms　点击 ({X1}, {Y1})　双状态校正　{Note}",
        "Swipe" => $"{WaitBeforeMs} ms　滑动 ({X1}, {Y1}) → ({X2}, {Y2})　普通滑动　{Note}",
        "Wheel" => $"{WaitBeforeMs} ms　滚轮{(WheelDelta > 0 ? "向上" : "向下")} {Math.Max(1, Math.Abs(WheelDelta) / 120)} 格　{Note}",
        _ => $"{WaitBeforeMs} ms　未知操作　{Note}"
    };
}

public sealed class TaskFlow
{
    public string Name { get; set; } = "新任务";
    public List<ActionStep> Steps { get; set; } = [];
    public override string ToString() => Name;
}

public sealed class MainForm : Form
{
    const int WM_HOTKEY = 0x0312;
    const int HOTKEY_RECORD = 1, HOTKEY_STOP_RECORD = 2, HOTKEY_EMERGENCY = 3;
    readonly ListBox tasks = new() { Dock = DockStyle.Fill };
    readonly ListBox steps = new() { Dock = DockStyle.Fill };
    readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly NumericUpDown loops = new() { Minimum = 1, Maximum = 99999, Value = 1, Width = 90 };
    readonly NumericUpDown gap = new() { Minimum = 0, Maximum = 3600, Value = 3, Width = 90 };
    readonly Button recordButton = new() { Text = "开始录制", AutoSize = true };
    readonly Button stopButton = new() { Text = "停止录制", AutoSize = true, Enabled = false };
    readonly Button playButton = new() { Text = "启动后台任务", AutoSize = true };
    readonly Button stopPlayButton = new() { Text = "停止任务", AutoSize = true, Enabled = false };
    readonly Button testButton = new() { Text = "测试PC连接", AutoSize = true };
    readonly CheckBox debugMode = new() { Text = "调试模式（每步暂停）", AutoSize = true, Padding = new Padding(10, 7, 0, 0) };
    readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(22, 28, 35) };
    readonly Label previewInfo = new() { Dock = DockStyle.Top, Height = 32, Text = "调试预览：尚未运行", TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
    readonly Button debugNext = new() { Text = "执行下一步", AutoSize = true, Enabled = false };
    readonly Button debugRetry = new() { Text = "重试本步", AutoSize = true, Enabled = false };
    readonly Button editPoint = new() { Text = "修改落点", AutoSize = true, Enabled = false };
    readonly ContextMenuStrip stepMenu = new();
    readonly string dataFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GF2Recorder", "tasks.json");
    readonly string legacyDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LDRecorder");
    List<TaskFlow> flows = [];
    MouseRecorder? recorder;
    CancellationTokenSource? playbackCts;
    IntPtr gameWindow;
    int playingStepIndex = -1;
    Bitmap? previewBitmap;
    TaskCompletionSource<bool>? debugGate;
    ActionStep? debugStep;
    bool editingPoint;
    double? lastRecognitionScore;
    string? lastRecognitionSummary;

    public MainForm()
    {
        Text = "少前2后台任务助手";
        Width = 1120; Height = 760; MinimumSize = new(900, 620);
        Font = new Font("Microsoft YaHei UI", 10F);
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BuildUi();
        ApplyTheme();
        Load += (_, _) => InitializeApp();
        FormClosing += (_, _) => { playbackCts?.Cancel(); debugGate?.TrySetCanceled(); recorder?.Dispose(); previewBitmap?.Dispose(); GameClient.RestoreParkedWindow(gameWindow); Save(); };
        tasks.SelectedIndexChanged += (_, _) => RefreshSteps();
        recordButton.Click += (_, _) => StartRecording();
        stopButton.Click += (_, _) => StopRecording();
        playButton.Click += async (_, _) => await PlayAsync();
        testButton.Click += async (_, _) => await TestConnectionAsync();
        stopPlayButton.Click += (_, _) => StopPlayback();
        debugNext.Click += (_, _) => debugGate?.TrySetResult(true);
        editPoint.Click += (_, _) => { editingPoint = !editingPoint; editPoint.Text = editingPoint ? "请在预览中选点" : "修改落点"; preview.Cursor = editingPoint ? Cursors.Cross : Cursors.Default; };
        debugRetry.Click += async (_, _) => await RetryDebugStepAsync();
        preview.MouseMove += PreviewMouseMove;
        preview.MouseClick += PreviewMouseClick;
        preview.Paint += PreviewPaint;
    }

    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 2, ColumnCount = 1, Padding = new Padding(0, 0, 0, 8) };
        toolbar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        toolbar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var taskRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var runRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
        var add = new Button { Text = "新建任务", AutoSize = true };
        var rename = new Button { Text = "重命名", AutoSize = true };
        var delete = new Button { Text = "删除任务", AutoSize = true };
        add.Click += (_, _) => AddTask(); rename.Click += (_, _) => RenameTask(); delete.Click += (_, _) => DeleteTask();
        taskRow.Controls.AddRange([add, rename, delete, testButton, recordButton, stopButton, playButton, stopPlayButton]);
        runRow.Controls.AddRange([
            new Label { Text = "循环设置", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 7, 10, 0) },
            new Label { Text = "循环次数", AutoSize = true, Padding = new Padding(0, 7, 4, 0) }, loops,
            new Label { Text = "次", AutoSize = true, Padding = new Padding(0, 7, 16, 0) },
            new Label { Text = "循环间隔", AutoSize = true, Padding = new Padding(0, 7, 4, 0) }, gap,
            new Label { Text = "秒", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, debugMode
        ]);
        toolbar.Controls.Add(taskRow, 0, 0);
        toolbar.Controls.Add(runRow, 0, 1);
        root.Controls.Add(toolbar, 0, 0); root.SetColumnSpan(toolbar, 2);

        var left = new GroupBox { Text = "任务流程", Dock = DockStyle.Fill, Padding = new Padding(8) }; left.Controls.Add(tasks);
        var right = new GroupBox { Text = "操作步骤与调试预览", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        var stepHeader = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(235, 241, 247) };
        stepHeader.Paint += DrawStepHeader;
        stepHeader.Resize += (_, _) => stepHeader.Invalidate();
        rightSplit.Panel1.Controls.Add(steps);
        rightSplit.Panel1.Controls.Add(stepHeader);
        var previewToolbar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(8, 4, 0, 0) };
        previewToolbar.Controls.AddRange([debugRetry, editPoint, debugNext]);
        rightSplit.Panel2.Controls.Add(preview);
        rightSplit.Panel2.Controls.Add(previewInfo);
        rightSplit.Panel2.Controls.Add(previewToolbar);
        right.Controls.Add(rightSplit);
        Shown += (_, _) =>
        {
            if (rightSplit.Height > 360)
                rightSplit.SplitterDistance = Math.Clamp((int)(rightSplit.Height * 0.52), 170, rightSplit.Height - 180);
        };
        var editWait = new ToolStripMenuItem("修改等待时间…");
        var editCoordinates = new ToolStripMenuItem("修改坐标…");
        var editNote = new ToolStripMenuItem("添加/修改备注…");
        var smartConfirm = new ToolStripMenuItem("设为智能确认…");
        var stateCorrection = new ToolStripMenuItem("设为双状态校正…");
        var cancelSmart = new ToolStripMenuItem("取消智能确认");
        var deleteStep = new ToolStripMenuItem("删除这步操作");
        editWait.Click += (_, _) => EditSelectedWait();
        editCoordinates.Click += (_, _) => EditSelectedCoordinates();
        editNote.Click += (_, _) => EditSelectedNote();
        smartConfirm.Click += async (_, _) => await ConfigureSmartConfirmAsync();
        stateCorrection.Click += async (_, _) => await ConfigureStateCorrectionAsync();
        cancelSmart.Click += (_, _) => CancelSmartConfirm();
        deleteStep.Click += (_, _) => DeleteSelectedStep();
        stepMenu.Items.AddRange([editWait, editCoordinates, editNote, smartConfirm, stateCorrection, cancelSmart, new ToolStripSeparator(), deleteStep]);
        stepMenu.Opening += (_, e) =>
        {
            if (steps.SelectedIndex < 0) { e.Cancel = true; return; }
            var type = Current?.Steps[steps.SelectedIndex].Type;
            editNote.Text = string.IsNullOrWhiteSpace(Current?.Steps[steps.SelectedIndex].Note) ? "添加备注…" : "修改备注…";
            smartConfirm.Enabled = type is "Tap" or "SmartTap";
            stateCorrection.Enabled = type is "Tap" or "SmartTap" or "StateCorrection";
            cancelSmart.Visible = type is "SmartTap" or "StateCorrection";
        };
        steps.ContextMenuStrip = stepMenu;
        steps.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right) { var i = steps.IndexFromPoint(e.Location); if (i >= 0) steps.SelectedIndex = i; } };
        steps.DrawMode = DrawMode.OwnerDrawFixed;
        steps.DrawItem += DrawStep;
        var previewMenu = new ContextMenuStrip();
        var previewNote = new ToolStripMenuItem("为当前步骤添加/修改备注…");
        previewNote.Click += (_, _) => { if (playingStepIndex >= 0) { steps.SelectedIndex = playingStepIndex; EditSelectedNote(); } };
        previewMenu.Items.Add(previewNote);
        previewMenu.Opening += (_, e) => { if (playingStepIndex < 0 || Current == null) e.Cancel = true; };
        preview.ContextMenuStrip = previewMenu;
        root.Controls.Add(left, 0, 1); root.Controls.Add(right, 1, 1);
        root.Controls.Add(status, 0, 2); root.SetColumnSpan(status, 2);
        Controls.Add(root);
    }

    void ApplyTheme()
    {
        BackColor = Color.FromArgb(245, 248, 252);
        tasks.BorderStyle = BorderStyle.FixedSingle;
        steps.BorderStyle = BorderStyle.FixedSingle;
        tasks.IntegralHeight = false;
        steps.IntegralHeight = false;
        tasks.ItemHeight = 30;
        steps.ItemHeight = 28;
        foreach (var button in GetAllControls(this).OfType<Button>())
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(205, 215, 226);
            button.FlatAppearance.BorderSize = 1;
            button.BackColor = Color.White;
            button.ForeColor = Color.FromArgb(35, 48, 62);
            button.Height = 34;
            button.Padding = new Padding(6, 0, 6, 0);
            button.Margin = new Padding(0, 0, 8, 0);
        }
        playButton.BackColor = Color.FromArgb(21, 153, 104); playButton.ForeColor = Color.White;
        stopPlayButton.BackColor = Color.FromArgb(220, 74, 74); stopPlayButton.ForeColor = Color.White;
        status.BackColor = Color.White;
        status.Padding = new Padding(10, 0, 0, 0);
    }

    static IEnumerable<Control> GetAllControls(Control root)
    {
        foreach (Control c in root.Controls) { yield return c; foreach (var child in GetAllControls(c)) yield return child; }
    }

    static (int Wait, int Operation, int State) StepColumns(int width)
    {
        var usable = Math.Max(0, width - 30);
        var wait = Math.Min(105, Math.Max(80, usable / 6));
        var operation = Math.Min(225, Math.Max(170, usable / 3));
        var state = Math.Min(195, Math.Max(155, usable / 4));
        return (wait, operation, state);
    }

    void DrawStepHeader(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel panel) return;
        var (wait, operation, state) = StepColumns(panel.ClientSize.Width);
        var x = 28;
        DrawCell("等待", wait);
        DrawCell("操作", operation);
        DrawCell("状态", state);
        DrawCell("备注", Math.Max(0, panel.ClientSize.Width - x));
        void DrawCell(string text, int width)
        {
            var rect = new Rectangle(x, 0, width, panel.ClientSize.Height);
            TextRenderer.DrawText(e.Graphics, text, Font, rect, Color.FromArgb(70, 84, 99), TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            using var line = new Pen(Color.FromArgb(210, 220, 230));
            e.Graphics.DrawLine(line, x + width - 1, 5, x + width - 1, panel.ClientSize.Height - 5);
            x += width;
        }
    }

    void DrawStep(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= steps.Items.Count) return;
        var active = e.Index == playingStepIndex;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var back = active ? Color.FromArgb(226, 247, 238) : selected ? Color.FromArgb(224, 235, 250) : Color.White;
        using var background = new SolidBrush(back);
        e.Graphics.FillRectangle(background, e.Bounds);
        var step = (ActionStep)steps.Items[e.Index];
        if (active)
        {
            var oldMode = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var centerY = e.Bounds.Y + e.Bounds.Height / 2f;
            var arrow = new[]
            {
                new PointF(e.Bounds.X + 9, centerY - 6),
                new PointF(e.Bounds.X + 17, centerY),
                new PointF(e.Bounds.X + 9, centerY + 6)
            };
            using var arrowBrush = new SolidBrush(Color.FromArgb(21, 153, 104));
            e.Graphics.FillPolygon(arrowBrush, arrow);
            e.Graphics.SmoothingMode = oldMode;
        }
        var color = active ? Color.FromArgb(23, 92, 67) : Color.FromArgb(35, 48, 62);
        var (waitWidth, operationWidth, stateWidth) = StepColumns(e.Bounds.Width);
        var x = e.Bounds.X + 28;
        DrawCell($"{step.WaitBeforeMs} ms", waitWidth);
        DrawCell(StepOperation(step), operationWidth);
        DrawCell(StepState(step), stateWidth);
        DrawCell(step.Note ?? "", Math.Max(0, e.Bounds.Right - x - 4));
        void DrawCell(string text, int width)
        {
            if (width <= 0) return;
            var rect = new Rectangle(x + 4, e.Bounds.Y, Math.Max(0, width - 8), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, Font, rect, color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using var line = new Pen(Color.FromArgb(225, 232, 239));
            e.Graphics.DrawLine(line, x + width - 1, e.Bounds.Y + 4, x + width - 1, e.Bounds.Bottom - 4);
            x += width;
        }
        if (active)
        {
            using var accent = new SolidBrush(Color.FromArgb(21, 153, 104));
            e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);
        }
    }

    static string StepOperation(ActionStep step) => step.Type switch
    {
        "Swipe" => $"滑动 ({step.X1},{step.Y1})→({step.X2},{step.Y2})",
        "Wheel" => $"滚轮{(step.WheelDelta > 0 ? "向上" : "向下")} {Math.Max(1, Math.Abs(step.WheelDelta) / 120)}格",
        "SmartTap" => $"出现后点击 ({step.X1},{step.Y1})",
        "StateCorrection" => $"识别后点击 ({step.X1},{step.Y1})",
        _ => $"点击 ({step.X1},{step.Y1})"
    };

    static string StepState(ActionStep step) => step.Type switch
    {
        "SmartTap" => $"智能确认·{(step.MatchMode == "BrightUI" ? "动态白字" : "标准图像")}·{(step.RetryTrigger ? $"{step.RetryIntervalMs / 1000.0:0.#}秒重试" : "只等待")}",
        "StateCorrection" => "双状态校正",
        "Swipe" => "普通滑动",
        "Wheel" => "鼠标滚轮",
        _ => "普通点击"
    };

    Rectangle GetPreviewImageRect()
    {
        if (previewBitmap == null || preview.ClientSize.Width <= 0 || preview.ClientSize.Height <= 0) return Rectangle.Empty;
        var scale = Math.Min((double)preview.ClientSize.Width / previewBitmap.Width, (double)preview.ClientSize.Height / previewBitmap.Height);
        var w = (int)(previewBitmap.Width * scale); var h = (int)(previewBitmap.Height * scale);
        return new Rectangle((preview.ClientSize.Width - w) / 2, (preview.ClientSize.Height - h) / 2, w, h);
    }

    Point? PreviewToGame(Point p)
    {
        var r = GetPreviewImageRect(); if (r.IsEmpty || !r.Contains(p)) return null;
        return new Point(Math.Clamp((int)((p.X - r.X) * 1920.0 / r.Width), 0, 1919), Math.Clamp((int)((p.Y - r.Y) * 1080.0 / r.Height), 0, 1079));
    }

    void PreviewMouseMove(object? sender, MouseEventArgs e)
    {
        var p = PreviewToGame(e.Location);
        if (p != null) previewInfo.Text = $"鼠标坐标：({p.Value.X}, {p.Value.Y})　当前步骤：{(playingStepIndex >= 0 ? playingStepIndex + 1 : 0)}" +
            (!string.IsNullOrWhiteSpace(debugStep?.Note) ? $"　备注：{debugStep.Note}" : "");
    }

    void PreviewMouseClick(object? sender, MouseEventArgs e)
    {
        if (!editingPoint || debugStep == null || e.Button != MouseButtons.Left) return;
        var p = PreviewToGame(e.Location); if (p == null) return;
        debugStep.X1 = p.Value.X; debugStep.Y1 = p.Value.Y;
        editingPoint = false; editPoint.Text = "修改落点"; preview.Cursor = Cursors.Default;
        if (Current != null && playingStepIndex >= 0) { steps.Items[playingStepIndex] = debugStep; steps.SelectedIndex = playingStepIndex; Save(); }
        previewInfo.Text = $"已修改步骤 {playingStepIndex + 1} 的点击位置：({debugStep.X1}, {debugStep.Y1})";
        preview.Invalidate();
    }

    void PreviewPaint(object? sender, PaintEventArgs e)
    {
        if (previewBitmap == null || debugStep == null) return;
        var r = GetPreviewImageRect(); if (r.IsEmpty) return;
        var x = r.X + debugStep.X1 * r.Width / 1920f; var y = r.Y + debugStep.Y1 * r.Height / 1080f;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(editingPoint ? Color.LimeGreen : Color.OrangeRed, 3);
        e.Graphics.DrawEllipse(pen, x - 11, y - 11, 22, 22);
        e.Graphics.DrawLine(pen, x - 17, y, x + 17, y); e.Graphics.DrawLine(pen, x, y - 17, x, y + 17);
        using var font = new Font(Font.FontFamily, 9, FontStyle.Bold);
        var label = $"步骤 {playingStepIndex + 1}  ({debugStep.X1}, {debugStep.Y1})";
        var size = e.Graphics.MeasureString(label, font);
        using var bg = new SolidBrush(Color.FromArgb(210, 20, 24, 30)); using var fg = new SolidBrush(Color.White);
        e.Graphics.FillRectangle(bg, x + 16, y - 26, size.Width + 10, size.Height + 6);
        e.Graphics.DrawString(label, font, fg, x + 21, y - 23);
    }

    void SetPreview(Bitmap bitmap, ActionStep step)
    {
        var old = previewBitmap; previewBitmap = bitmap; preview.Image = bitmap; old?.Dispose();
        debugStep = step;
        var actionText = step.Type switch
        {
            "Swipe" => $"滑动 ({step.X1}, {step.Y1}) → ({step.X2}, {step.Y2})",
            "Wheel" => $"滚轮{(step.WheelDelta > 0 ? "向上" : "向下")} {Math.Max(1, Math.Abs(step.WheelDelta) / 120)} 格",
            _ => $"点击 ({step.X1}, {step.Y1})"
        };
        previewInfo.Text = $"步骤 {playingStepIndex + 1}　{actionText}" +
            (step.Type is "SmartTap" or "StateCorrection" && !string.IsNullOrWhiteSpace(lastRecognitionSummary)
                ? $"　｜{lastRecognitionSummary}" : "") +
            (!string.IsNullOrWhiteSpace(step.Note) ? $"　｜备注：{step.Note}" : "");
        debugRetry.Enabled = true; editPoint.Enabled = step.Type is "Tap" or "SmartTap" or "StateCorrection" or "Wheel"; debugNext.Enabled = true; preview.Invalidate();
    }

    void ClearPreview()
    {
        preview.Image = null; previewBitmap?.Dispose(); previewBitmap = null; debugStep = null; editingPoint = false;
        previewInfo.Text = "调试预览：尚未运行"; debugRetry.Enabled = editPoint.Enabled = debugNext.Enabled = false; preview.Invalidate();
    }

    async Task RetryDebugStepAsync()
    {
        if (debugStep == null || !EnsureGameWindow(true)) return;
        debugRetry.Enabled = false;
        try
        {
            await ExecuteBasicStepAsync(debugStep, CancellationToken.None);
            await Task.Delay(300);
            SetPreview(await GameClient.CaptureAsync(gameWindow, CancellationToken.None), debugStep);
        }
        catch (Exception ex) { SetStatus("重试失败：" + ex.Message, true); }
        finally { debugRetry.Enabled = true; }
    }

    async Task DebugPauseAsync(ActionStep step, CancellationToken token)
    {
        if (!debugMode.Checked || !EnsureGameWindow(false)) return;
        SetPreview(await GameClient.CaptureAsync(gameWindow, token), step);
        debugGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => debugGate.TrySetCanceled(token));
        await debugGate.Task;
        debugGate = null; debugNext.Enabled = false; debugRetry.Enabled = false; editPoint.Enabled = false;
    }

    Task ExecuteBasicStepAsync(ActionStep step, CancellationToken token) => step.Type switch
    {
        "Swipe" => GameClient.SwipeAsync(gameWindow, step.X1, step.Y1, step.X2, step.Y2, step.DurationMs, token),
        "Wheel" => GameClient.WheelAsync(gameWindow, step.X1, step.Y1, step.WheelDelta, token),
        _ => GameClient.TapAsync(gameWindow, step.X1, step.Y1, token)
    };

    void InitializeApp()
    {
        const uint ctrlAlt = 0x0001 | 0x0002;
        Native.RegisterHotKey(Handle, HOTKEY_RECORD, ctrlAlt, (int)Keys.F8);
        Native.RegisterHotKey(Handle, HOTKEY_STOP_RECORD, ctrlAlt, (int)Keys.F9);
        Native.RegisterHotKey(Handle, HOTKEY_EMERGENCY, ctrlAlt, (int)Keys.F10);
        MigrateLegacyData();
        LoadData();
        gameWindow = GameClient.FindWindow();
        SetStatus(gameWindow == IntPtr.Zero
            ? "未找到《少女前线2》PC客户端。打开游戏后即可录制或运行任务。"
            : "已连接《少女前线2》PC客户端（GF2_Exilium.exe）。Ctrl+Alt+F8录制，Ctrl+Alt+F9停止，Ctrl+Alt+F10紧急停止。");
    }

    async Task TestConnectionAsync()
    {
        if (!EnsureGameWindow(true)) return;
        testButton.Enabled = false;
        try
        {
            var bitmap = await GameClient.CaptureAsync(gameWindow, CancellationToken.None);
            var old = previewBitmap; previewBitmap = bitmap; preview.Image = bitmap; old?.Dispose();
            debugStep = null; previewInfo.Text = $"PC连接测试成功：{GameClient.LastCaptureMode}，截图 {bitmap.Width}×{bitmap.Height}";
            SetStatus("已捕获PC客户端画面。请检查下方预览是否为当前游戏画面；后台点击需再用无消耗任务验证。");
        }
        catch (Exception ex) { SetStatus("PC连接测试失败：" + ex.Message, true); MessageBox.Show(ex.Message, "PC后台连接测试"); }
        finally { GameClient.RestoreParkedWindow(gameWindow); testButton.Enabled = true; }
    }

    bool EnsureGameWindow(bool showMessage)
    {
        if (gameWindow != IntPtr.Zero && Native.IsWindow(gameWindow)) return true;
        gameWindow = GameClient.FindWindow();
        if (gameWindow != IntPtr.Zero) return true;
        if (showMessage) MessageBox.Show("没有找到《少女前线2》PC客户端窗口。\n请先启动游戏并进入主界面。", "未连接游戏");
        return false;
    }

    void MigrateLegacyData()
    {
        var newDir = Path.GetDirectoryName(dataFile)!;
        if (File.Exists(dataFile) || !Directory.Exists(legacyDataDir)) return;
        try
        {
            Directory.CreateDirectory(newDir);
            foreach (var source in Directory.EnumerateFiles(legacyDataDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(legacyDataDir, source);
                var destination = Path.Combine(newDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, true);
            }
            if (File.Exists(dataFile))
            {
                var json = File.ReadAllText(dataFile).Replace(legacyDataDir.Replace("\\", "\\\\"), newDir.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(dataFile, json);
            }
        }
        catch (Exception ex) { MessageBox.Show("旧版任务迁移未完全完成：" + ex.Message + "\n旧数据没有删除，可以稍后重新迁移。", "任务迁移"); }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case HOTKEY_RECORD: StartRecording(); break;
                case HOTKEY_STOP_RECORD: StopRecording(); break;
                case HOTKEY_EMERGENCY: if (recorder != null) StopRecording(); StopPlayback(); break;
            }
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (IsHandleCreated) { Native.UnregisterHotKey(Handle, HOTKEY_RECORD); Native.UnregisterHotKey(Handle, HOTKEY_STOP_RECORD); Native.UnregisterHotKey(Handle, HOTKEY_EMERGENCY); }
        base.Dispose(disposing);
    }

    TaskFlow? Current => tasks.SelectedItem as TaskFlow;
    void AddTask() { var f = new TaskFlow { Name = $"重复任务 {flows.Count + 1}" }; flows.Add(f); tasks.Items.Add(f); tasks.SelectedItem = f; Save(); }
    void RenameTask() { if (Current is not { } f) return; var name = Prompt.Show("任务名称", f.Name); if (!string.IsNullOrWhiteSpace(name)) { f.Name = name.Trim(); tasks.Items[tasks.SelectedIndex] = f; Save(); } }
    void DeleteTask() { if (Current is not { } f || MessageBox.Show($"确定删除“{f.Name}”吗？", "确认", MessageBoxButtons.YesNo) != DialogResult.Yes) return; flows.Remove(f); tasks.Items.Remove(f); Save(); }
    void DeleteSelectedStep()
    {
        if (Current is not { } f || steps.SelectedIndex < 0) return;
        var step = f.Steps[steps.SelectedIndex];
        if (!string.IsNullOrWhiteSpace(step.TemplateFile) && File.Exists(step.TemplateFile)) try { File.Delete(step.TemplateFile); } catch { }
        if (!string.IsNullOrWhiteSpace(step.AlternateTemplateFile) && File.Exists(step.AlternateTemplateFile)) try { File.Delete(step.AlternateTemplateFile); } catch { }
        f.Steps.RemoveAt(steps.SelectedIndex); RefreshSteps(); Save();
    }
    void EditSelectedWait()
    {
        if (Current is not { } f || steps.SelectedIndex < 0) return;
        var index = steps.SelectedIndex;
        var step = f.Steps[index];
        var value = Prompt.ShowNumber("修改等待时间", "执行这一步之前等待多少毫秒：", step.WaitBeforeMs, 0, 600000);
        if (value is null) return;
        step.WaitBeforeMs = value.Value;
        steps.Items[index] = step;
        steps.SelectedIndex = index;
        Save();
    }

    void EditSelectedCoordinates()
    {
        if (Current is not { } flow || steps.SelectedIndex < 0) return;
        var index = steps.SelectedIndex;
        var step = flow.Steps[index];
        using var dialog = new CoordinateDialog(step);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        step.X1 = dialog.X1; step.Y1 = dialog.Y1;
        if (step.Type == "Swipe") { step.X2 = dialog.X2; step.Y2 = dialog.Y2; }
        steps.Items[index] = step; steps.SelectedIndex = index; Save();
        SetStatus(step.Type switch
        {
            "Swipe" => $"已修改步骤 {index + 1}：起点 ({step.X1}, {step.Y1})，终点 ({step.X2}, {step.Y2})",
            "Wheel" => $"已修改步骤 {index + 1} 的滚轮位置：({step.X1}, {step.Y1})",
            _ => $"已修改步骤 {index + 1} 的点击坐标：({step.X1}, {step.Y1})"
        });
    }

    void EditSelectedNote()
    {
        if (Current is not { } flow || steps.SelectedIndex < 0) return;
        var index = steps.SelectedIndex;
        var step = flow.Steps[index];
        var note = Prompt.Show("步骤备注", step.Note ?? "");
        if (note == null) return;
        step.Note = note.Trim();
        steps.Items[index] = step; steps.SelectedIndex = index; Save();
        SetStatus(string.IsNullOrWhiteSpace(step.Note) ? $"已清除步骤 {index + 1} 的备注。" : $"已保存步骤 {index + 1} 的备注：{step.Note}");
    }

    async Task ConfigureSmartConfirmAsync()
    {
        if (Current is not { } flow || steps.SelectedIndex < 0) return;
        var index = steps.SelectedIndex;
        var step = flow.Steps[index];
        if (step.Type is not ("Tap" or "SmartTap")) { MessageBox.Show("只有点击步骤可以设置为智能确认。"); return; }
        if (!EnsureGameWindow(true)) return;

        ActionStep? previousTap = null;
        for (var i = index - 1; i >= 0; i--)
            if (flow.Steps[i].Type is "Tap" or "SmartTap") { previousTap = flow.Steps[i]; break; }

        using var dialog = new SmartConfirmDialog(step, previousTap != null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            step.TimeoutMs = dialog.TimeoutMs;
            step.CheckIntervalMs = dialog.CheckIntervalMs;
            step.MatchThreshold = dialog.MatchThreshold;
            step.MatchMode = dialog.BrightUiMode ? "BrightUI" : "Standard";
            step.RetryTrigger = dialog.RetryTrigger && previousTap != null;
            step.RetryIntervalMs = dialog.RetryIntervalMs;
            if (previousTap != null) { step.RetryX = previousTap.X1; step.RetryY = previousTap.Y1; }
            if (dialog.KeepExistingTemplate)
            {
                step.Type = "SmartTap";
                steps.Items[index] = step; steps.SelectedIndex = index; Save();
                SetStatus("智能确认参数已更新，原识别区域和模板已保留。");
                return;
            }
            SetStatus("正在从PC客户端截取确认按钮模板…");
            using var screen = await GameClient.CaptureAsync(gameWindow, CancellationToken.None);
            using var selector = new RegionSelector(screen, "框选需要识别的稳定区域（尽量只包含按钮或文字）");
            if (selector.ShowDialog(this) != DialogResult.OK || selector.SelectedRegion.Width < 12 || selector.SelectedRegion.Height < 12) return;
            var selected = selector.SelectedRegion;
            var x = selected.X; var y = selected.Y; var cropWidth = selected.Width; var cropHeight = selected.Height;
            var templateDir = Path.Combine(Path.GetDirectoryName(dataFile)!, "templates");
            Directory.CreateDirectory(templateDir);
            var templateFile = Path.Combine(templateDir, Guid.NewGuid().ToString("N") + ".png");
            using (var crop = screen.Clone(new Rectangle(x, y, cropWidth, cropHeight), screen.PixelFormat)) crop.Save(templateFile, System.Drawing.Imaging.ImageFormat.Png);
            if (!string.IsNullOrWhiteSpace(step.TemplateFile) && File.Exists(step.TemplateFile)) try { File.Delete(step.TemplateFile); } catch { }
            step.Type = "SmartTap";
            step.TemplateFile = templateFile;
            step.MatchX = x; step.MatchY = y; step.MatchWidth = cropWidth; step.MatchHeight = cropHeight;
            steps.Items[index] = step; steps.SelectedIndex = index; Save();
            SetStatus("智能确认已设置。回放时会等待画面出现后再点击。");
        }
        catch (Exception ex) { SetStatus("模板截取失败：" + ex.Message, true); }
    }

    async Task ConfigureStateCorrectionAsync()
    {
        if (Current is not { } flow || steps.SelectedIndex < 0 || !EnsureGameWindow(true)) return;
        var index = steps.SelectedIndex; var step = flow.Steps[index];
        if (step.Type is not ("Tap" or "SmartTap" or "StateCorrection")) return;
        if (MessageBox.Show("第一步：请先让游戏显示“正确状态”（例如普通已选中），然后点击确定开始截图。", "双状态校正", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        try
        {
            using var desiredScreen = await GameClient.CaptureAsync(gameWindow, CancellationToken.None);
            using var selector = new RegionSelector(desiredScreen, "框选能区分两种状态的小区域，例如普通/困难页签");
            if (selector.ShowDialog(this) != DialogResult.OK) return;
            var region = selector.SelectedRegion;
            var dir = Path.Combine(Path.GetDirectoryName(dataFile)!, "templates"); Directory.CreateDirectory(dir);
            var desiredFile = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".png");
            using (var crop = desiredScreen.Clone(region, desiredScreen.PixelFormat)) crop.Save(desiredFile, System.Drawing.Imaging.ImageFormat.Png);

            if (MessageBox.Show("第二步：现在请在游戏中切换到“错误状态”（例如困难已选中），完成后点击确定。", "双状态校正", MessageBoxButtons.OKCancel) != DialogResult.OK) { File.Delete(desiredFile); return; }
            using var alternateScreen = await GameClient.CaptureAsync(gameWindow, CancellationToken.None);
            var alternateFile = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".png");
            using (var crop = alternateScreen.Clone(region, alternateScreen.PixelFormat)) crop.Save(alternateFile, System.Drawing.Imaging.ImageFormat.Png);
            using var pointSelector = new PointSelector(alternateScreen, "点击用于恢复正确状态的位置，例如“普通”页签");
            if (pointSelector.ShowDialog(this) != DialogResult.OK) { File.Delete(desiredFile); File.Delete(alternateFile); return; }

            foreach (var old in new[] { step.TemplateFile, step.AlternateTemplateFile }) if (!string.IsNullOrWhiteSpace(old) && File.Exists(old)) try { File.Delete(old); } catch { }
            step.Type = "StateCorrection"; step.TemplateFile = desiredFile; step.AlternateTemplateFile = alternateFile;
            step.MatchX = region.X; step.MatchY = region.Y; step.MatchWidth = region.Width; step.MatchHeight = region.Height;
            step.CorrectX = pointSelector.SelectedPoint.X; step.CorrectY = pointSelector.SelectedPoint.Y;
            step.TimeoutMs = 30000; step.CheckIntervalMs = 500; step.MatchThreshold = 0.80;
            steps.Items[index] = step; steps.SelectedIndex = index; Save();
            SetStatus("双状态校正已设置：正确状态直接确认，错误状态先校正再确认。");
        }
        catch (Exception ex) { SetStatus("状态校正设置失败：" + ex.Message, true); }
    }

    void CancelSmartConfirm()
    {
        if (Current is not { } flow || steps.SelectedIndex < 0) return;
        var index = steps.SelectedIndex; var step = flow.Steps[index];
        if (step.Type is not ("SmartTap" or "StateCorrection")) return;
        if (!string.IsNullOrWhiteSpace(step.TemplateFile) && File.Exists(step.TemplateFile)) try { File.Delete(step.TemplateFile); } catch { }
        if (!string.IsNullOrWhiteSpace(step.AlternateTemplateFile) && File.Exists(step.AlternateTemplateFile)) try { File.Delete(step.AlternateTemplateFile); } catch { }
        step.Type = "Tap"; step.TemplateFile = null; step.RetryTrigger = false;
        step.AlternateTemplateFile = null;
        steps.Items[index] = step; steps.SelectedIndex = index; Save();
    }
    void RefreshSteps() { steps.Items.Clear(); if (Current is { } f) foreach (var s in f.Steps) steps.Items.Add(s); }

    void StartRecording()
    {
        if (recorder != null || playbackCts != null) return;
        if (Current is not { } flow) { MessageBox.Show("请先新建或选择一个任务。"); return; }
        if (!EnsureGameWindow(true)) return;
        var render = gameWindow;
        if (flow.Steps.Count > 0 && MessageBox.Show("当前任务已有步骤。是否清空后重新录制？\n选择“否”会继续追加。", "录制", MessageBoxButtons.YesNo) == DialogResult.Yes) flow.Steps.Clear();
        recorder = new MouseRecorder(render, step => BeginInvoke(() => { flow.Steps.Add(step); steps.Items.Add(step); }));
        recorder.Start(); recordButton.Enabled = false; stopButton.Enabled = true; playButton.Enabled = false; testButton.Enabled = false;
        SetStatus("正在录制：请在《少女前线2》PC客户端内操作。Ctrl+Alt+F9停止录制。");
    }

    void StopRecording()
    {
        if (recorder == null) return;
        recorder.Dispose(); recorder = null; recordButton.Enabled = true; stopButton.Enabled = false; playButton.Enabled = true; testButton.Enabled = true; Save();
        SetStatus($"录制完成，共 {Current?.Steps.Count ?? 0} 步。可以启动PC后台任务。");
    }

    async Task PlayAsync()
    {
        if (!EnsureGameWindow(true)) return;
        if (Current is not { Steps.Count: > 0 } flow) { MessageBox.Show("当前任务没有录制步骤。"); return; }
        playbackCts = new(); TogglePlaying(true);
        try
        {
            using (var connectionTest = await GameClient.CaptureAsync(gameWindow, playbackCts.Token)) { }
            SetStatus($"PC后台连接测试通过（{GameClient.LastCaptureMode}）。开始运行“{flow.Name}”。");
            for (int n = 1; n <= (int)loops.Value; n++)
            {
                SetStatus($"正在后台运行“{flow.Name}”：第 {n}/{loops.Value} 次");
                for (var i = 0; i < flow.Steps.Count; i++)
                {
                    var s = flow.Steps[i];
                    lastRecognitionScore = null; lastRecognitionSummary = null;
                    playingStepIndex = i;
                    steps.SelectedIndex = i;
                    steps.TopIndex = Math.Max(0, i - 3);
                    steps.Invalidate();
                    SetStatus($"正在后台运行“{flow.Name}”：第 {n}/{loops.Value} 次，步骤 {i + 1}/{flow.Steps.Count}");
                    await Task.Delay(s.WaitBeforeMs, playbackCts.Token);
                    if (s.Type == "SmartTap")
                    {
                        var matched = await WaitForSmartConfirmAsync(s, flow.Name, n, i, playbackCts.Token);
                        if (!matched) throw new TimeoutException($"步骤 {i + 1} 等待确认框超时，任务已暂停。");
                        SetStatus($"智能确认成功：步骤 {i + 1}/{flow.Steps.Count}，识别相似度 {lastRecognitionScore:P1}（要求 {s.MatchThreshold:P0}），正在点击 ({s.X1}, {s.Y1})");
                        await GameClient.TapAsync(gameWindow, s.X1, s.Y1, playbackCts.Token);
                    }
                    else if (s.Type == "StateCorrection")
                    {
                        await ExecuteStateCorrectionAsync(s, flow.Name, n, i, playbackCts.Token);
                    }
                    else
                    {
                        await ExecuteBasicStepAsync(s, playbackCts.Token);
                    }
                    await DebugPauseAsync(s, playbackCts.Token);
                }
                if (n < loops.Value) await Task.Delay((int)gap.Value * 1000, playbackCts.Token);
            }
            SetStatus($"任务“{flow.Name}”运行完成。");
        }
        catch (OperationCanceledException) { SetStatus("已停止运行。", true); }
        catch (Exception ex) { System.Media.SystemSounds.Exclamation.Play(); SetStatus("运行失败：" + ex.Message, true); }
        finally { GameClient.RestoreParkedWindow(gameWindow); playingStepIndex = -1; steps.Invalidate(); ClearPreview(); playbackCts?.Dispose(); playbackCts = null; TogglePlaying(false); }
    }

    void StopPlayback() { debugGate?.TrySetCanceled(); playbackCts?.Cancel(); }
    async Task<bool> WaitForSmartConfirmAsync(ActionStep step, string flowName, int loopNo, int stepIndex, CancellationToken token)
    {
        if (!EnsureGameWindow(false) || string.IsNullOrWhiteSpace(step.TemplateFile) || !File.Exists(step.TemplateFile))
            throw new InvalidOperationException("智能确认模板不存在，请重新设置这一步。");
        using var template = new Bitmap(step.TemplateFile);
        var timer = Stopwatch.StartNew();
        long lastRetry = -step.RetryIntervalMs;
        var retryCount = 0;
        while (timer.ElapsedMilliseconds < step.TimeoutMs)
        {
            token.ThrowIfCancellationRequested();
            using var screen = await GameClient.CaptureAsync(gameWindow, token);
            var score = ImageMatcher.Similarity(screen, template, step.MatchX, step.MatchY, step.MatchMode);
            lastRecognitionScore = score;
            var retryText = step.RetryTrigger ? $"，已重试上一步 {retryCount} 次" : "，仅等待不重试";
            SetStatus($"智能确认检测中：步骤 {stepIndex + 1}，当前 {score:P1} / 要求 {step.MatchThreshold:P0}{retryText}");
            if (score >= step.MatchThreshold)
            {
                lastRecognitionSummary = $"确认成功　相似度 {score:P1} / 阈值 {step.MatchThreshold:P0}";
                return true;
            }
            if (step.RetryTrigger && timer.ElapsedMilliseconds - lastRetry >= step.RetryIntervalMs)
            {
                await GameClient.TapAsync(gameWindow, step.RetryX, step.RetryY, token);
                retryCount++;
                lastRetry = timer.ElapsedMilliseconds;
            }
            await Task.Delay(step.CheckIntervalMs, token);
        }
        lastRecognitionSummary = $"确认失败　最高/最后相似度 {lastRecognitionScore:P1} / 阈值 {step.MatchThreshold:P0}";
        return false;
    }
    async Task ExecuteStateCorrectionAsync(ActionStep step, string flowName, int loopNo, int stepIndex, CancellationToken token)
    {
        if (!EnsureGameWindow(false) || string.IsNullOrWhiteSpace(step.TemplateFile) || string.IsNullOrWhiteSpace(step.AlternateTemplateFile) || !File.Exists(step.TemplateFile) || !File.Exists(step.AlternateTemplateFile))
            throw new InvalidOperationException("状态校正模板不存在，请重新设置。");
        using var desired = new Bitmap(step.TemplateFile); using var alternate = new Bitmap(step.AlternateTemplateFile);
        var timer = Stopwatch.StartNew(); long lastCorrection = -3000;
        const double decisionMargin = 0.08;
        while (timer.ElapsedMilliseconds < step.TimeoutMs)
        {
            using var screen = await GameClient.CaptureAsync(gameWindow, token);
            var desiredScore = ImageMatcher.Similarity(screen, desired, step.MatchX, step.MatchY);
            var alternateScore = ImageMatcher.Similarity(screen, alternate, step.MatchX, step.MatchY);
            var lead = Math.Abs(desiredScore - alternateScore);
            var isDesired = desiredScore >= step.MatchThreshold && desiredScore >= alternateScore + decisionMargin;
            var isAlternate = alternateScore >= step.MatchThreshold && alternateScore >= desiredScore + decisionMargin;
            var verdict = isDesired ? "当前处于目标状态" : isAlternate ? "当前处于需校正状态" : "暂时无法确定，继续识别";
            SetStatus($"双状态识别：目标状态 {desiredScore:P1}，需校正状态 {alternateScore:P1}，领先差 {lead:P1} → {verdict}");
            if (isDesired)
            {
                lastRecognitionScore = desiredScore;
                lastRecognitionSummary = $"当前是目标状态　匹配 {desiredScore:P1}（另一状态 {alternateScore:P1}）";
                SetStatus($"识别结果：当前处于【目标状态】，匹配度 {desiredScore:P1}；需校正状态仅 {alternateScore:P1}。正在点击确认。");
                await GameClient.TapAsync(gameWindow, step.X1, step.Y1, token); return;
            }
            if (isAlternate && timer.ElapsedMilliseconds - lastCorrection >= 2000)
            {
                lastRecognitionScore = alternateScore;
                lastRecognitionSummary = $"当前是需校正状态　匹配 {alternateScore:P1}，已执行校正";
                SetStatus($"识别结果：当前处于【需校正状态】，匹配度 {alternateScore:P1}；目标状态仅 {desiredScore:P1}。正在点击校正位置，随后会重新识别。");
                await GameClient.TapAsync(gameWindow, step.CorrectX, step.CorrectY, token);
                lastCorrection = timer.ElapsedMilliseconds; await Task.Delay(600, token); continue;
            }
            await Task.Delay(step.CheckIntervalMs, token);
        }
        throw new TimeoutException($"步骤 {stepIndex + 1} 无法识别或校正状态，任务已暂停。");
    }
    void TogglePlaying(bool playing) { playButton.Enabled = !playing; recordButton.Enabled = !playing; testButton.Enabled = !playing; stopPlayButton.Enabled = playing; tasks.Enabled = !playing; debugMode.Enabled = !playing; }
    void SetStatus(string text, bool error = false) { status.Text = text; status.ForeColor = error ? Color.Firebrick : Color.FromArgb(30, 90, 150); }

    void LoadData()
    {
        try { if (File.Exists(dataFile)) flows = JsonSerializer.Deserialize<List<TaskFlow>>(File.ReadAllText(dataFile)) ?? []; } catch { flows = []; }
        if (flows.Count == 0) flows.Add(new TaskFlow { Name = "重复任务 1" });
        foreach (var f in flows) tasks.Items.Add(f); tasks.SelectedIndex = 0;
    }
    void Save() { try { Directory.CreateDirectory(Path.GetDirectoryName(dataFile)!); File.WriteAllText(dataFile, JsonSerializer.Serialize(flows, new JsonSerializerOptions { WriteIndented = true })); } catch { } }
}

public sealed class MouseRecorder : IDisposable
{
    readonly IntPtr renderWindow;
    readonly Action<ActionStep> onStep;
    readonly Stopwatch clock = Stopwatch.StartNew();
    Native.LowLevelMouseProc? callback;
    IntPtr hook;
    long lastAction;
    Native.POINT down;
    long downAt;
    bool pressed;

    public MouseRecorder(IntPtr renderWindow, Action<ActionStep> onStep) { this.renderWindow = renderWindow; this.onStep = onStep; }
    public void Start() { callback = Hook; using var p = Process.GetCurrentProcess(); using var m = p.MainModule!; hook = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(m.ModuleName), 0); }
    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if (wParam == (IntPtr)0x0201 && Native.PointInWindow(renderWindow, info.pt)) { down = info.pt; downAt = clock.ElapsedMilliseconds; pressed = true; }
            else if (wParam == (IntPtr)0x0202 && pressed)
            {
                pressed = false;
                if (Native.PointInWindow(renderWindow, info.pt))
                {
                    var a = Native.ToGame(renderWindow, down); var b = Native.ToGame(renderWindow, info.pt);
                    var dist = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                    var now = clock.ElapsedMilliseconds; var wait = (int)Math.Clamp(downAt - lastAction, 0, 600000); lastAction = now;
                    onStep(dist < 15 ? new ActionStep { Type = "Tap", X1 = a.X, Y1 = a.Y, WaitBeforeMs = wait }
                        : new ActionStep { Type = "Swipe", X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, DurationMs = (int)Math.Clamp(now - downAt, 100, 10000), WaitBeforeMs = wait });
                }
            }
            else if (wParam == (IntPtr)0x020A && Native.PointInWindow(renderWindow, info.pt))
            {
                var point = Native.ToGame(renderWindow, info.pt);
                var delta = unchecked((short)(info.mouseData >> 16));
                if (delta != 0)
                {
                    var now = clock.ElapsedMilliseconds;
                    var wait = (int)Math.Clamp(now - lastAction, 0, 600000);
                    lastAction = now;
                    onStep(new ActionStep { Type = "Wheel", X1 = point.X, Y1 = point.Y, WheelDelta = delta, WaitBeforeMs = wait });
                }
            }
        }
        return Native.CallNextHookEx(hook, code, wParam, lParam);
    }
    public void Dispose() { if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; callback = null; }
}

static class GameClient
{
    const int WM_ACTIVATE = 0x0006, WM_SETFOCUS = 0x0007, WA_ACTIVE = 1;
    const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEWHEEL = 0x020A, MK_LBUTTON = 0x0001;
    static Native.RECT parkedOriginal;
    static bool parked, wasMinimized;
    public static string LastCaptureMode { get; private set; } = "窗口后台截图";

    public static IntPtr FindWindow()
    {
        var processes = Process.GetProcessesByName("GF2_Exilium");
        foreach (var process in processes)
        {
            try { if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle; }
            catch { }
        }
        return Native.FindWindowByProcess("GF2_Exilium");
    }

    public static async Task TapAsync(IntPtr window, int x, int y, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = Native.FindInputWindow(window);
        var p = TargetClientPoint(window, target, x, y);
        var lp = MakeLParam(p.X, p.Y);
        PrepareBackgroundInput(window, target);
        SendMouse(target, WM_MOUSEMOVE, IntPtr.Zero, lp);
        SendMouse(target, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp);
        await Task.Delay(65, token);
        SendMouse(target, WM_LBUTTONUP, IntPtr.Zero, lp);
        await Task.Delay(25, token);
    }

    public static async Task SwipeAsync(IntPtr window, int x1, int y1, int x2, int y2, int durationMs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = Native.FindInputWindow(window);
        var a = TargetClientPoint(window, target, x1, y1); var b = TargetClientPoint(window, target, x2, y2);
        PrepareBackgroundInput(window, target);
        SendMouse(target, WM_MOUSEMOVE, IntPtr.Zero, MakeLParam(a.X, a.Y));
        SendMouse(target, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, MakeLParam(a.X, a.Y));
        await Task.Delay(40, token);
        var frames = Math.Clamp(durationMs / 16, 4, 120);
        for (var i = 1; i <= frames; i++)
        {
            token.ThrowIfCancellationRequested();
            var x = a.X + (b.X - a.X) * i / frames; var y = a.Y + (b.Y - a.Y) * i / frames;
            SendMouse(target, WM_MOUSEMOVE, (IntPtr)MK_LBUTTON, MakeLParam(x, y));
            await Task.Delay(Math.Max(1, durationMs / frames), token);
        }
        SendMouse(target, WM_LBUTTONUP, IntPtr.Zero, MakeLParam(b.X, b.Y));
        await Task.Delay(25, token);
    }

    public static async Task WheelAsync(IntPtr window, int x, int y, int delta, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = Native.FindInputWindow(window);
        var client = TargetClientPoint(window, target, x, y);
        var screen = RootScreenPoint(window, x, y);
        PrepareBackgroundInput(window, target);
        SendMouse(target, WM_MOUSEMOVE, IntPtr.Zero, MakeLParam(client.X, client.Y));
        SendMouse(target, WM_MOUSEWHEEL, (IntPtr)(delta << 16), MakeLParam(screen.X, screen.Y));
        await Task.Delay(35, token);
    }

    static void PrepareBackgroundInput(IntPtr root, IntPtr target)
    {
        Native.PostMessage(root, WM_ACTIVATE, (IntPtr)WA_ACTIVE, IntPtr.Zero);
        Native.PostMessage(target, WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
    }

    static void SendMouse(IntPtr target, int message, IntPtr wParam, IntPtr lParam)
    {
        if (Native.SendMessageTimeout(target, message, wParam, lParam, 0x0002, 200, out _) == IntPtr.Zero)
            Native.PostMessage(target, message, wParam, lParam);
    }

    static Point TargetClientPoint(IntPtr root, IntPtr target, int x, int y)
    {
        var screen = RootScreenPoint(root, x, y);
        var p = new Native.POINT { X = screen.X, Y = screen.Y };
        Native.ScreenToClient(target, ref p);
        return new Point(p.X, p.Y);
    }

    static Point RootScreenPoint(IntPtr root, int x, int y)
    {
        var client = ScalePoint(root, x, y);
        var p = new Native.POINT { X = client.X, Y = client.Y };
        Native.ClientToScreen(root, ref p);
        return new Point(p.X, p.Y);
    }

    public static Task<Bitmap> CaptureAsync(IntPtr window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Native.IsWindow(window)) throw new InvalidOperationException("游戏窗口已经关闭。");
        var minimized = Native.IsIconic(window);
        LastCaptureMode = minimized ? "真正最小化截图" : "窗口后台截图";
        Bitmap bitmap;
        try { bitmap = Capture(window); }
        catch when (minimized)
        {
            ParkMinimizedWindow(window);
            Thread.Sleep(250);
            bitmap = Capture(window);
            LastCaptureMode = "屏幕外后台运行";
        }
        if (minimized && !parked && !LooksUsable(bitmap))
        {
            bitmap.Dispose();
            ParkMinimizedWindow(window);
            Thread.Sleep(250);
            bitmap = Capture(window);
            LastCaptureMode = "屏幕外后台运行";
        }
        if (!LooksUsable(bitmap)) { bitmap.Dispose(); throw new InvalidOperationException("无法读取游戏画面。游戏最小化后可能停止渲染，请尝试让窗口保持打开或使用屏幕外后台模式。"); }
        if (bitmap.Width != 1920 || bitmap.Height != 1080)
        {
            var normalized = new Bitmap(1920, 1080);
            using var g = Graphics.FromImage(normalized); g.DrawImage(bitmap, 0, 0, 1920, 1080); bitmap.Dispose(); bitmap = normalized;
        }
        return Task.FromResult(bitmap);
    }

    static Bitmap Capture(IntPtr window)
    {
        if (!Native.GetClientRect(window, out var rect) || rect.Right <= 0 || rect.Bottom <= 0) throw new InvalidOperationException("无法读取游戏客户区尺寸。");
        var bitmap = new Bitmap(rect.Right, rect.Bottom, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var dc = graphics.GetHdc();
        try
        {
            if (!Native.PrintWindow(window, dc, 3))
                throw new InvalidOperationException("PC客户端拒绝后台截图。");
        }
        finally { graphics.ReleaseHdc(dc); }
        return bitmap;
    }

    static bool LooksUsable(Bitmap bitmap)
    {
        var bright = 0; var varied = 0; var samples = 0; Color? previous = null;
        for (var y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 30))
        for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 40))
        {
            var c = bitmap.GetPixel(x, y); samples++;
            if (c.R + c.G + c.B > 30) bright++;
            if (previous is { } p && Math.Abs(c.R-p.R) + Math.Abs(c.G-p.G) + Math.Abs(c.B-p.B) > 12) varied++;
            previous = c;
        }
        return samples > 0 && bright > samples / 20 && varied > samples / 30;
    }

    static void ParkMinimizedWindow(IntPtr window)
    {
        if (!parked)
        {
            Native.GetWindowRect(window, out parkedOriginal); wasMinimized = Native.IsIconic(window); parked = true;
        }
        Native.ShowWindow(window, 4); // SW_SHOWNOACTIVATE
        Native.SetWindowPos(window, new IntPtr(1), Screen.PrimaryScreen!.Bounds.Right - 2, Screen.PrimaryScreen.Bounds.Bottom - 2, 0, 0, 0x0001 | 0x0010); // NOSIZE | NOACTIVATE
    }

    public static void RestoreParkedWindow(IntPtr window)
    {
        if (!parked || !Native.IsWindow(window)) return;
        Native.SetWindowPos(window, IntPtr.Zero, parkedOriginal.Left, parkedOriginal.Top, parkedOriginal.Right-parkedOriginal.Left, parkedOriginal.Bottom-parkedOriginal.Top, 0x0010 | 0x0004);
        if (wasMinimized) Native.ShowWindow(window, 6); // SW_MINIMIZE
        parked = false;
    }

    static Point ScalePoint(IntPtr window, int x, int y)
    {
        Native.GetClientRect(window, out var rect);
        return new Point(Math.Clamp((int)Math.Round(x * Math.Max(1, rect.Right) / 1920.0), 0, Math.Max(0, rect.Right-1)), Math.Clamp((int)Math.Round(y * Math.Max(1, rect.Bottom) / 1080.0), 0, Math.Max(0, rect.Bottom-1)));
    }
    static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xffff));
}

static class ImageMatcher
{
    public static double Similarity(Bitmap screen, Bitmap template, int x, int y, string mode = "Standard")
    {
        if (x < 0 || y < 0 || x + template.Width > screen.Width || y + template.Height > screen.Height) return 0;
        if (mode == "BrightUI") return BrightUiSimilarity(screen, template, x, y);
        // UI 状态识别不能用“总 RGB 差值 / 255”的宽松算法：大面积颜色互换仍会得到约 80%。
        // 改为逐像素相符度：15 以内视为完全一致，75 以上视为完全不一致，中间线性衰减。
        const int sampleStep = 3;
        double similarity = 0; long samples = 0;
        for (var py = 0; py < template.Height; py += sampleStep)
        for (var px = 0; px < template.Width; px += sampleStep)
        {
            var a = template.GetPixel(px, py); var b = screen.GetPixel(x + px, y + py);
            var pixelDifference = (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 3.0;
            similarity += Math.Clamp(1.0 - (pixelDifference - 15.0) / 60.0, 0, 1);
            samples++;
        }
        return samples == 0 ? 0 : similarity / samples;
    }
    static double BrightUiSimilarity(Bitmap screen, Bitmap template, int x, int y)
    {
        const int sampleStep = 3; long expected = 0, matched = 0;
        for (var py=0; py<template.Height; py+=sampleStep)
        for (var px=0; px<template.Width; px+=sampleStep)
        {
            var a=template.GetPixel(px,py); var amin=Math.Min(a.R,Math.Min(a.G,a.B)); var amax=Math.Max(a.R,Math.Max(a.G,a.B));
            if ((a.R+a.G+a.B)/3 < 175 || amax-amin > 85) continue;
            expected++; var b=screen.GetPixel(x+px,y+py); var bmin=Math.Min(b.R,Math.Min(b.G,b.B)); var bmax=Math.Max(b.R,Math.Max(b.G,b.B));
            if ((b.R+b.G+b.B)/3 >= 150 && bmax-bmin <= 105) matched++;
        }
        return expected < 8 ? 0 : matched/(double)expected;
    }
}

public sealed class SmartConfirmDialog : Form
{
    readonly NumericUpDown timeout = new() { Minimum = 5, Maximum = 600, Value = 30, Width = 90 };
    readonly NumericUpDown interval = new() { Minimum = 200, Maximum = 5000, Increment = 100, Value = 500, Width = 90 };
    readonly NumericUpDown threshold = new() { Minimum = 50, Maximum = 99, Value = 82, Width = 90 };
    readonly CheckBox retry = new() { Text = "确认框未出现时，重复点击前一个触发步骤", AutoSize = true };
    readonly CheckBox brightUi = new() { Text = "动态背景上的白色按钮/文字（例如“跳过”）", AutoSize = true };
    readonly CheckBox keepTemplate = new() { Text = "保留现有识别区域和模板（只修改上面的参数）", AutoSize = true };
    readonly NumericUpDown retryInterval = new() { Minimum = 1, Maximum = 60, Value = 3, Width = 90 };
    public int TimeoutMs => (int)timeout.Value * 1000;
    public int CheckIntervalMs => (int)interval.Value;
    public double MatchThreshold => (double)threshold.Value / 100.0;
    public bool RetryTrigger => retry.Checked;
    public int RetryIntervalMs => (int)retryInterval.Value * 1000;
    public bool BrightUiMode => brightUi.Checked;
    public bool KeepExistingTemplate => keepTemplate.Visible && keepTemplate.Checked;

    public SmartConfirmDialog(ActionStep step, bool hasPreviousTap)
    {
        var hasExistingTemplate = step.Type == "SmartTap" && !string.IsNullOrWhiteSpace(step.TemplateFile) && File.Exists(step.TemplateFile);
        Text = hasExistingTemplate ? "编辑智能确认（已设置）" : "设置智能确认";
        ClientSize = new Size(620, 455); StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; Font = new Font("Microsoft YaHei UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        timeout.Value = Math.Clamp(step.TimeoutMs / 1000, 5, 600);
        interval.Value = Math.Clamp(step.CheckIntervalMs, 200, 5000);
        threshold.Value = Math.Clamp((decimal)(step.MatchThreshold * 100), 50, 99);
        retry.Checked = step.RetryTrigger && hasPreviousTap; retry.Enabled = hasPreviousTap;
        brightUi.Checked = step.MatchMode == "BrightUI";
        retryInterval.Value = Math.Clamp(step.RetryIntervalMs / 1000, 1, 60);

        var info = new Label
        {
            Left = 20, Top = 18, Width = 570, Height = 66,
            Text = hasExistingTemplate
                ? "这一步已经设置过智能确认。参数已自动回填；勾选“保留现有模板”时不会要求重新框选。"
                : "请先让PC客户端停留在“确认框已经出现”的画面。保存后，请框选需要识别的稳定按钮或文字区域。"
        };
        AddRow("最长等待", timeout, "秒", 96);
        AddRow("检查间隔", interval, "毫秒", 136);
        AddRow("识别相似度", threshold, "%（建议 82）", 176);
        brightUi.Left = 20; brightUi.Top = 215;
        retry.Left = 20; retry.Top = 245;
        var retryLabel = new Label { Left = 42, Top = 285, Width = 90, Text = "重试间隔" };
        retryInterval.Left = 140; retryInterval.Top = 280;
        var retryUnit = new Label { Left = 238, Top = 285, Width = 50, Text = "秒" };
        retryInterval.Enabled = retry.Checked;
        retry.CheckedChanged += (_, _) => retryInterval.Enabled = retry.Checked;
        keepTemplate.Left = 20; keepTemplate.Top = 325; keepTemplate.Visible = hasExistingTemplate; keepTemplate.Checked = hasExistingTemplate;
        var ok = new Button { Text = hasExistingTemplate ? "保存设置" : "保存并截取模板", Left = 390, Top = 395, Width = 125, Height = 38, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
        var cancel = new Button { Text = "取消", Left = 525, Top = 395, Width = 75, Height = 38, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
        Controls.AddRange([info, brightUi, retry, retryLabel, retryInterval, retryUnit, keepTemplate, ok, cancel]); AcceptButton = ok; CancelButton = cancel;
    }

    void AddRow(string name, Control input, string unit, int top)
    {
        Controls.Add(new Label { Left = 20, Top = top + 5, Width = 110, Text = name });
        input.Left = 140; input.Top = top; Controls.Add(input);
        Controls.Add(new Label { Left = 238, Top = top + 5, Width = 180, Text = unit });
    }
}

public sealed class CoordinateDialog : Form
{
    readonly NumericUpDown x1 = CoordinateInput();
    readonly NumericUpDown y1 = CoordinateInput();
    readonly NumericUpDown x2 = CoordinateInput();
    readonly NumericUpDown y2 = CoordinateInput();
    public int X1 => (int)x1.Value;
    public int Y1 => (int)y1.Value;
    public int X2 => (int)x2.Value;
    public int Y2 => (int)y2.Value;

    public CoordinateDialog(ActionStep step)
    {
        var isSwipe = step.Type == "Swipe";
        var isWheel = step.Type == "Wheel";
        Text = isSwipe ? "修改滑动坐标" : isWheel ? "修改滚轮位置" : "修改点击坐标";
        ClientSize = new Size(430, isSwipe ? 250 : 175);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; Font = new Font("Microsoft YaHei UI", 10F); AutoScaleMode = AutoScaleMode.Dpi;
        x1.Value = Math.Clamp(step.X1, 0, 1919); y1.Value = Math.Clamp(step.Y1, 0, 1079);
        x2.Value = Math.Clamp(step.X2, 0, 1919); y2.Value = Math.Clamp(step.Y2, 0, 1079);
        AddCoordinateRow(isSwipe ? "滑动起点" : isWheel ? "滚轮位置" : "点击位置", x1, y1, 25);
        if (isSwipe) AddCoordinateRow("滑动终点", x2, y2, 92);
        var note = new Label { Left = 22, Top = isSwipe ? 150 : 83, Width = 380, Text = "坐标范围：X 0–1919，Y 0–1079" };
        var buttonTop = isSwipe ? 195 : 120;
        var ok = new Button { Text = "保存", Left = 260, Top = buttonTop, Width = 75, Height = 34, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Left = 345, Top = buttonTop, Width = 75, Height = 34, DialogResult = DialogResult.Cancel };
        Controls.AddRange([note, ok, cancel]); AcceptButton = ok; CancelButton = cancel;
    }

    static NumericUpDown CoordinateInput() => new() { Minimum = 0, Maximum = 1919, Width = 95 };
    void AddCoordinateRow(string title, NumericUpDown x, NumericUpDown y, int top)
    {
        Controls.Add(new Label { Left = 22, Top = top + 5, Width = 90, Text = title });
        Controls.Add(new Label { Left = 118, Top = top + 5, Width = 22, Text = "X" });
        x.Left = 140; x.Top = top; Controls.Add(x);
        Controls.Add(new Label { Left = 248, Top = top + 5, Width = 22, Text = "Y" });
        y.Left = 270; y.Top = top; y.Maximum = 1079; Controls.Add(y);
    }
}

public sealed class RegionSelector : Form
{
    const int ToolbarHeight = 70;
    readonly Bitmap image;
    Point dragStart, dragEnd;
    bool dragging;
    readonly Button save = SelectorButton("请先框选区域", 125, false);
    public Rectangle SelectedRegion { get; private set; }

    public RegionSelector(Bitmap source, string title)
    {
        image = new Bitmap(source); Text = title; WindowState = FormWindowState.Maximized; FormBorderStyle = FormBorderStyle.Sizable;
        BackColor = Color.FromArgb(16, 20, 26); DoubleBuffered = true; Cursor = Cursors.Cross;
        save.DialogResult = DialogResult.OK;
        var hint = new Label { Text = title + "；拖动鼠标框选，Enter确认，Esc取消", ForeColor = Color.FromArgb(35,48,62), BackColor = Color.FromArgb(242,247,252), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16,0,8,0) };
        var cancel = SelectorButton("取消", 80, true); cancel.DialogResult = DialogResult.Cancel;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 235, WrapContents = false, BackColor = Color.FromArgb(242,247,252), Padding = new Padding(6,10,6,6) };
        var toolbar = new Panel { Dock = DockStyle.Top, Height = ToolbarHeight, BackColor = Color.FromArgb(242,247,252), Cursor = Cursors.Default };
        actions.Controls.AddRange([save, cancel]); toolbar.Controls.Add(hint); toolbar.Controls.Add(actions); Controls.Add(toolbar); AcceptButton = save; CancelButton = cancel;
        Resize += (_, _) => Invalidate();
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && ImageRect().Contains(e.Location)) { dragStart = dragEnd = e.Location; dragging = true; save.Enabled = false; } };
        MouseMove += (_, e) => { if (dragging) { dragEnd = e.Location; Invalidate(); } };
        MouseUp += (_, e) => { if (dragging) { dragEnd = e.Location; dragging = false; SelectedRegion = ToImageRect(Normalize(dragStart, dragEnd)); save.Enabled = SelectedRegion.Width >= 12 && SelectedRegion.Height >= 12; save.Text = save.Enabled ? "使用此区域" : "请重新框选"; Invalidate(); } };
    }

    Rectangle ImageRect()
    {
        var availableHeight = Math.Max(1, ClientSize.Height - ToolbarHeight);
        var scale = Math.Min((double)ClientSize.Width / image.Width, (double)availableHeight / image.Height);
        var w = (int)(image.Width * scale);
        var h = (int)(image.Height * scale);
        return new Rectangle((ClientSize.Width - w) / 2, ToolbarHeight + (availableHeight - h) / 2, w, h);
    }
    static Rectangle Normalize(Point a, Point b) => Rectangle.FromLTRB(Math.Min(a.X,b.X), Math.Min(a.Y,b.Y), Math.Max(a.X,b.X), Math.Max(a.Y,b.Y));
    Rectangle ToImageRect(Rectangle r)
    {
        var ir = ImageRect(); r.Intersect(ir); if (r.IsEmpty) return Rectangle.Empty;
        return Rectangle.FromLTRB((int)((r.Left-ir.Left)*image.Width/(double)ir.Width), (int)((r.Top-ir.Top)*image.Height/(double)ir.Height), (int)((r.Right-ir.Left)*image.Width/(double)ir.Width), (int)((r.Bottom-ir.Top)*image.Height/(double)ir.Height));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var ir = ImageRect(); e.Graphics.DrawImage(image, ir);
        var r = Normalize(dragStart, dragEnd); if (r.Width > 1 && r.Height > 1) { using var fill = new SolidBrush(Color.FromArgb(55, 0, 190, 255)); using var pen = new Pen(Color.Cyan, 2); e.Graphics.FillRectangle(fill, r); e.Graphics.DrawRectangle(pen, r); }
    }
    protected override void Dispose(bool disposing) { if (disposing) image.Dispose(); base.Dispose(disposing); }
    internal static Button SelectorButton(string text, int width, bool enabled)
    {
        var button = new Button { Text = text, Width = width, Height = 38, Enabled = enabled, UseVisualStyleBackColor = false, BackColor = Color.White, ForeColor = Color.FromArgb(25, 32, 40), FlatStyle = FlatStyle.Flat, Margin = new Padding(4) };
        button.FlatAppearance.BorderColor = Color.FromArgb(0, 190, 255); button.FlatAppearance.BorderSize = 2;
        return button;
    }
}

public sealed class PointSelector : Form
{
    const int ToolbarHeight = 70;
    readonly Bitmap image; Point selected; bool hasPoint;
    readonly Button save = RegionSelector.SelectorButton("请先选择位置", 125, false);
    public Point SelectedPoint => selected;
    public PointSelector(Bitmap source, string title)
    {
        image = new Bitmap(source); Text = title; WindowState = FormWindowState.Maximized; BackColor = Color.FromArgb(16,20,26); DoubleBuffered = true; Cursor = Cursors.Cross;
        save.DialogResult = DialogResult.OK;
        var hint = new Label { Text = title + "；单击选择，Enter确认，Esc取消", ForeColor = Color.FromArgb(35,48,62), BackColor = Color.FromArgb(242,247,252), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16,0,8,0) };
        var cancel = RegionSelector.SelectorButton("取消", 80, true); cancel.DialogResult = DialogResult.Cancel;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 235, WrapContents = false, BackColor = Color.FromArgb(242,247,252), Padding = new Padding(6,10,6,6) };
        var toolbar = new Panel { Dock = DockStyle.Top, Height = ToolbarHeight, BackColor = Color.FromArgb(242,247,252), Cursor = Cursors.Default };
        actions.Controls.AddRange([save,cancel]); toolbar.Controls.Add(hint); toolbar.Controls.Add(actions); Controls.Add(toolbar); AcceptButton = save; CancelButton = cancel;
        Resize += (_,_) => Invalidate();
        MouseClick += (_,e) => { var r=ImageRect(); if(e.Button!=MouseButtons.Left || !r.Contains(e.Location)) return; selected=new Point((int)((e.X-r.X)*image.Width/(double)r.Width),(int)((e.Y-r.Y)*image.Height/(double)r.Height)); hasPoint=true; save.Enabled=true; save.Text="使用此位置"; Invalidate(); };
    }
    Rectangle ImageRect(){var availableHeight=Math.Max(1,ClientSize.Height-ToolbarHeight);var scale=Math.Min((double)ClientSize.Width/image.Width,(double)availableHeight/image.Height);var w=(int)(image.Width*scale);var h=(int)(image.Height*scale);return new Rectangle((ClientSize.Width-w)/2,ToolbarHeight+(availableHeight-h)/2,w,h);}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);var r=ImageRect();e.Graphics.DrawImage(image,r);if(hasPoint){var x=r.X+selected.X*r.Width/(float)image.Width;var y=r.Y+selected.Y*r.Height/(float)image.Height;e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;using var p=new Pen(Color.Lime,3);e.Graphics.DrawEllipse(p,x-12,y-12,24,24);e.Graphics.DrawLine(p,x-18,y,x+18,y);e.Graphics.DrawLine(p,x,y-18,x,y+18);}}
    protected override void Dispose(bool disposing){if(disposing)image.Dispose();base.Dispose(disposing);}
}

static class Prompt
{
    public static string? Show(string title, string value)
    {
        using var f = new Form { Text = title, Width = 420, Height = 150, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var box = new TextBox { Left = 15, Top = 15, Width = 370, Text = value };
        var ok = new Button { Text = "确定", Left = 225, Top = 55, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Left = 310, Top = 55, Width = 75, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange([box, ok, cancel]); f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? box.Text : null;
    }

    public static int? ShowNumber(string title, string label, int value, int minimum, int maximum)
    {
        using var f = new Form { Text = title, Width = 440, Height = 175, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, Font = new Font("Microsoft YaHei UI", 10F) };
        var text = new Label { Left = 15, Top = 18, Width = 390, Text = label };
        var number = new NumericUpDown { Left = 15, Top = 48, Width = 180, Minimum = minimum, Maximum = maximum, Value = Math.Clamp(value, minimum, maximum), ThousandsSeparator = true };
        var unit = new Label { Left = 205, Top = 51, Width = 80, Text = "毫秒" };
        var ok = new Button { Text = "保存", Left = 245, Top = 88, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Left = 330, Top = 88, Width = 75, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange([text, number, unit, ok, cancel]); f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? (int)number.Value : null;
    }
}

static class Native
{
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, int key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wp, IntPtr lp);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    public static IntPtr FindWindowByProcess(string processName)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static IntPtr FindInputWindow(IntPtr root)
    {
        if (!GetClientRect(root, out var rootRect)) return root;
        var rootArea = Math.Max(1L, (long)rootRect.Right * rootRect.Bottom);
        var best = IntPtr.Zero;
        long bestArea = 0;
        EnumChildWindows(root, (child, _) =>
        {
            if (!IsWindowVisible(child) || !GetClientRect(child, out var rect)) return true;
            var area = Math.Max(0L, (long)rect.Right * rect.Bottom);
            if (area > bestArea) { best = child; bestArea = area; }
            return true;
        }, IntPtr.Zero);
        return best != IntPtr.Zero && bestArea >= rootArea / 4 ? best : root;
    }
    public static bool PointInWindow(IntPtr h, POINT p) => GetWindowRect(h, out var r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    public static POINT ToGame(IntPtr h, POINT p)
    {
        ScreenToClient(h, ref p); GetClientRect(h, out var r);
        return new POINT { X = Math.Clamp((int)Math.Round(p.X * 1920.0 / Math.Max(1, r.Right)), 0, 1919), Y = Math.Clamp((int)Math.Round(p.Y * 1080.0 / Math.Max(1, r.Bottom)), 0, 1079) };
    }
}
