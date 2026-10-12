using System.Diagnostics;
using System.Text;

namespace TimerResTuner;

internal sealed class MainForm : Form
{
    private readonly NumericUpDown _start, _end, _coarse, _fine, _samples, _confirm, _applyMs;
    private readonly Button _run, _cancel, _apply, _install, _uninstall, _stopHolder, _globalToggle, _tickToggle, _export;
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 18 };
    private readonly Label _status = new() { AutoSize = true, Text = "Ready.", Padding = new Padding(0, 4, 0, 0) };
    private readonly Label _sysInfo = new() { AutoSize = true, Padding = new Padding(4) };
    private readonly Label _best = new() { AutoSize = true, Padding = new Padding(4, 6, 4, 6), Text = "No benchmark yet." };
    private readonly DataGridView _grid = new();
    private readonly ChartPanel _chart = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };

    private readonly List<BenchPoint> _points = new();
    private BenchPoint? _bestPoint;
    private CancellationTokenSource? _cts;

    private bool _globalOn, _taskInstalled, _tickDisabled;

    // Test-build stamp (see BuildInfo / build-tester.ps1). Unstamped builds never show the banner.
    private readonly Label _banner = new() { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 6, 8, 6), Margin = new Padding(0, 0, 0, 6) };
    private bool _wasExpired;
    private static bool Expired => BuildInfo.Current.IsExpired(DateTime.UtcNow);

    public MainForm()
    {
        Text = "TimerResTuner — find the best timer resolution";
        if (BuildInfo.Current.TesterId is { } tester)
            Text += $"  [test build for {tester}]";
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(1040, 760);
        MinimumSize = new Size(820, 600);
        StartPosition = FormStartPosition.CenterScreen;

        _start = Num(0.5m, 15m, 0.5m, 4, 0.001m);
        _end = Num(0.5m, 15.625m, 1.0m, 4, 0.001m);
        _coarse = Num(0.001m, 1m, 0.025m, 4, 0.001m);
        _fine = Num(0.0001m, 0.1m, 0.002m, 4, 0.0001m);
        _samples = Num(5, 1000, 40, 0, 5);
        _confirm = Num(10, 5000, 150, 0, 10);
        _applyMs = Num(0.5m, 15.625m, 0.5m, 4, 0.0001m);

        _run = Btn("Run benchmark", OnRun);
        _cancel = Btn("Cancel", (_, _) => _cts?.Cancel());
        _cancel.Enabled = false;
        _apply = Btn("Apply now", OnApply);
        _install = Btn("Apply at every logon", OnInstall);
        _uninstall = Btn("Remove from logon", OnUninstall);
        _stopHolder = Btn("Stop / restore default", OnStopHolder);
        _globalToggle = Btn("", OnGlobalToggle);
        _tickToggle = Btn("", OnTickToggle);
        _export = Btn("Export CSV", OnExport);

        _best.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);

        SetupGrid();

        var settings = Flow(
            Lbl("Start ms"), _start, Lbl("End ms"), _end, Lbl("Coarse step"), _coarse,
            Lbl("Fine step"), _fine, Lbl("Samples"), _samples, Lbl("Confirm samples"), _confirm, _run, _cancel);

        var applyRow = Flow(Lbl("Resolution ms"), _applyMs, _apply, _install, _uninstall, _stopHolder, _export);
        var systemRow = Flow(_globalToggle, _tickToggle);

        var sysBox = new GroupBox { Text = "System", Dock = DockStyle.Fill, AutoSize = true };
        var sysLayout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        sysLayout.Controls.Add(_sysInfo);
        sysLayout.Controls.Add(systemRow);
        sysBox.Controls.Add(sysLayout);

        var benchBox = new GroupBox { Text = "Benchmark", Dock = DockStyle.Fill, AutoSize = true };
        var benchLayout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        benchLayout.Controls.Add(settings);
        var progressRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        progressRow.Controls.Add(_progress, 0, 0);
        progressRow.Controls.Add(_status, 1, 0);
        benchLayout.Controls.Add(progressRow);
        benchBox.Controls.Add(benchLayout);

        var applyBox = new GroupBox { Text = "Apply", Dock = DockStyle.Fill, AutoSize = true };
        var applyLayout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        applyLayout.Controls.Add(_best);
        applyLayout.Controls.Add(applyRow);
        applyBox.Controls.Add(applyLayout);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(_chart);
        split.Panel2.Controls.Add(_grid);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
        if (BuildInfo.Current.IsTestBuild)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(_banner);
        }
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(sysBox);
        root.Controls.Add(benchBox);
        root.Controls.Add(applyBox);
        root.Controls.Add(split);
        Controls.Add(root);

        Load += (_, _) =>
        {
            split.SplitterDistance = split.Height / 2;
            RefreshSystemState();
            RefreshLive();
        };
        _refresh.Tick += (_, _) =>
        {
            RefreshLive();
            if (Expired != _wasExpired) ApplyBuildRestrictions(); // expiry can arrive while the window is open
        };
        _refresh.Start();
        FormClosing += (_, _) => _cts?.Cancel();
    }

    // ---------- Benchmark ----------

    private async void OnRun(object? sender, EventArgs e)
    {
        if (Expired) return;
        var s = new BenchmarkSettings
        {
            StartMs = (double)_start.Value,
            EndMs = (double)_end.Value,
            CoarseStepMs = (double)_coarse.Value,
            FineStepMs = (double)_fine.Value,
            Samples = (int)_samples.Value,
            ConfirmSamples = (int)_confirm.Value,
        };
        if (s.EndMs <= s.StartMs)
        {
            MessageBox.Show(this, "End must be greater than Start.", Text);
            return;
        }

        // Our own holder would pin the resolution and make every result identical.
        if (Holder.IsRunning)
        {
            SetStatus("Stopping running holder...");
            await Task.Run(() => Holder.Stop());
        }

        var info = TimerInfo.Query();
        bool sharedTimer = !SystemConfig.IsWindows11 || _globalOn;
        if (sharedTimer && info.CurrentMs < s.EndMs - 0.001)
        {
            var r = MessageBox.Show(this,
                $"Another process is currently holding the timer at {info.CurrentMs:0.0000} ms.\n\n" +
                "Timer requests are global on this system (GlobalTimerResolutionRequests), so every resolution " +
                $"coarser than {info.CurrentMs:0.0000} ms will actually run at {info.CurrentMs:0.0000} ms and can't be measured. " +
                "Those points will be greyed out and ignored.\n\n" +
                "For a valid result, close other timer tools (TimerResolution, ISLC, Process Lasso, etc.) and games first.\n\nRun anyway?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
        }

        _points.Clear();
        _grid.Rows.Clear();
        _bestPoint = null;
        _chart.SetData(_points.ToList(), null);
        _best.Text = "Benchmarking... leave the PC idle for best results.";

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var bench = new Benchmarker();
        bench.PointMeasured += p => BeginInvoke(() => AddPoint(p));
        bench.Progress += (done, total, msg) => BeginInvoke(() =>
        {
            _progress.Maximum = Math.Max(total, 1);
            _progress.Value = Math.Min(done, _progress.Maximum);
            SetStatus($"[{done}/{total}] {msg}");
        });

        var priorPriority = Process.GetCurrentProcess().PriorityClass;
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            var token = _cts.Token;
            var best = await RunOnThread(() => bench.Run(s, token));
            ShowBest(best);
            SetStatus("Done.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
            _best.Text = "Benchmark cancelled.";
        }
        catch (Exception ex)
        {
            SetStatus("Failed: " + ex.Message);
        }
        finally
        {
            Process.GetCurrentProcess().PriorityClass = priorPriority;
            SetBusy(false);
        }
    }

    private static Task<T> RunOnThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "TimerBenchmark",
        };
        t.Start();
        return tcs.Task;
    }

    private void AddPoint(BenchPoint p)
    {
        _points.Add(p);
        int i = _grid.Rows.Add(p.Masked ? p.Phase + " (masked)" : p.Phase, p.RequestedMs, p.ActualMs,
            p.MeanDelta, p.StdDev, p.P99Delta, p.MaxDelta, p.Score, p.Samples);
        if (p.Masked) _grid.Rows[i].DefaultCellStyle.ForeColor = Color.Gray;
        _chart.SetData(_points.ToList(), _bestPoint);
    }

    private void ShowBest(BenchPoint best)
    {
        _bestPoint = best;
        _applyMs.Value = Math.Clamp((decimal)best.RequestedMs, _applyMs.Minimum, _applyMs.Maximum);
        _chart.SetData(_points.ToList(), best);

        var sb = new StringBuilder($"Best: {best.RequestedMs:0.0000} ms  —  mean Δ {best.MeanDelta:0.0000} ms, stdev {best.StdDev:0.0000} ms");
        var baseline = _points.Where(p => p.Phase == "coarse").MinBy(p => p.RequestedUnits);
        if (baseline is not null && baseline.RequestedUnits != best.RequestedUnits)
            sb.Append($"   (vs {baseline.RequestedMs:0.000} ms: Δ {baseline.MeanDelta:0.0000}, stdev {baseline.StdDev:0.0000})");
        if (best.Masked)
            sb.Append($"   ⚠ masked by another process at {best.ActualMs:0.0000} ms — result not trustworthy");
        _best.Text = sb.ToString();

        foreach (DataGridViewRow row in _grid.Rows)
            if (row.Cells[0].Value is "confirm" && (double)row.Cells[1].Value == best.RequestedMs)
                row.DefaultCellStyle.BackColor = Color.FromArgb(220, 252, 231);
    }

    // ---------- Apply ----------

    private uint ApplyUnits => (uint)Math.Round((double)_applyMs.Value * 10000);

    private void OnApply(object? sender, EventArgs e)
    {
        if (Expired) return;
        WarnIfNotGlobal();
        Holder.Start(ApplyUnits);
        SetStatus($"Holding {_applyMs.Value:0.0000} ms in the background.");
    }

    private void OnInstall(object? sender, EventArgs e)
    {
        if (Expired) return;
        WarnIfNotGlobal();
        var (code, output) = SystemConfig.InstallStartupTask(ApplyUnits);
        if (code != 0)
        {
            MessageBox.Show(this, "Failed to create scheduled task:\n" + output, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Holder.Start(ApplyUnits);
        RefreshSystemState();
        SetStatus($"Installed: {_applyMs.Value:0.0000} ms will be applied at every logon (keep the exe at {Environment.ProcessPath}).");
    }

    private void OnUninstall(object? sender, EventArgs e)
    {
        var (code, output) = SystemConfig.RemoveStartupTask();
        RefreshSystemState();
        SetStatus(code == 0 ? "Logon task removed." : "Remove failed: " + output);
    }

    private void OnStopHolder(object? sender, EventArgs e)
    {
        Holder.Stop();
        SetStatus("Holder stopped; Windows default resolution restored (unless another app requests one).");
    }

    private void WarnIfNotGlobal()
    {
        if (!SystemConfig.IsWindows11 || _globalOn) return;
        var r = MessageBox.Show(this,
            "On Windows 11 a timer request only affects the process that made it, so the background holder " +
            "won't change the resolution CS2 sees unless GlobalTimerResolutionRequests is enabled.\n\n" +
            "Enable it now? (Reboot required.)",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (r == DialogResult.Yes)
        {
            SystemConfig.GlobalRequestsEnabled = true;
            RefreshSystemState();
        }
    }

    // ---------- System toggles ----------

    private void OnGlobalToggle(object? sender, EventArgs e)
    {
        if (Expired && !_globalOn) return; // an expired build may only turn it OFF
        SystemConfig.GlobalRequestsEnabled = !_globalOn;
        RefreshSystemState();
        MessageBox.Show(this, "GlobalTimerResolutionRequests updated. Reboot for it to take effect.", Text);
    }

    private void OnTickToggle(object? sender, EventArgs e)
    {
        if (Expired && !_tickDisabled) return; // an expired build may only re-enable dynamic tick (undo)
        string action = _tickDisabled ? "re-enable dynamic tick (Windows default)" : "disable dynamic tick (bcdedit /set disabledynamictick yes)";
        if (MessageBox.Show(this, $"This will {action}. It changes the boot configuration and needs a reboot.\n\nContinue?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        var (code, output) = SystemConfig.SetDynamicTickDisabled(!_tickDisabled);
        RefreshSystemState();
        MessageBox.Show(this, code == 0 ? "Done. Reboot to apply." : "bcdedit failed:\n" + output, Text);
    }

    private void OnExport(object? sender, EventArgs e)
    {
        if (_points.Count == 0) return;
        using var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "timer-benchmark.csv" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var sb = new StringBuilder("Phase,RequestedMs,ActualMs,Samples,MeanDeltaMs,StdDevMs,P99DeltaMs,MaxDeltaMs,Score\n");
        foreach (var p in _points)
            sb.AppendLine($"{p.Phase},{p.RequestedMs:0.0000},{p.ActualMs:0.0000},{p.Samples},{p.MeanDelta:0.00000},{p.StdDev:0.00000},{p.P99Delta:0.00000},{p.MaxDelta:0.00000},{p.Score:0.00000}");
        File.WriteAllText(dlg.FileName, sb.ToString());
        SetStatus("Exported " + dlg.FileName);
    }

    // ---------- State ----------

    // Slow checks (spawn schtasks / bcdedit): only on load and after changes.
    private void RefreshSystemState()
    {
        _globalOn = SystemConfig.GlobalRequestsEnabled;
        _taskInstalled = SystemConfig.StartupTaskInstalled;
        _tickDisabled = SystemConfig.DynamicTickDisabled;

        _globalToggle.Text = _globalOn ? "Disable GlobalTimerResolutionRequests" : "Enable GlobalTimerResolutionRequests";
        _tickToggle.Text = _tickDisabled ? "Re-enable dynamic tick" : "Disable dynamic tick";
        _uninstall.Enabled = _taskInstalled;
        RefreshLive();
        ApplyBuildRestrictions();
    }

    /// <summary>
    /// Shows the test-build banner and, once expired, switches to restore-only mode: nothing new can be
    /// applied, but everything can still be undone (stop the holder, remove the logon task, turn the
    /// two system tweaks off).
    /// </summary>
    private void ApplyBuildRestrictions()
    {
        var info = BuildInfo.Current;
        var now = DateTime.UtcNow;
        bool expired = info.IsExpired(now);
        _wasExpired = expired;

        if (info.IsTestBuild)
        {
            _banner.Text = info.Describe(now) +
                           (expired ? "  Restore-only mode: you can undo changes but not make new ones." : "");
            (_banner.BackColor, _banner.ForeColor) = expired ? (Color.FromArgb(253, 231, 233), Color.FromArgb(150, 20, 40))
                : info.NeedsAttention(now) ? (Color.FromArgb(255, 244, 206), Color.FromArgb(110, 80, 0))
                : (Color.FromArgb(232, 240, 254), Color.FromArgb(40, 60, 110));
        }

        if (!expired) return;

        foreach (var c in new Control[] { _run, _apply, _install, _start, _end, _coarse, _fine, _samples, _confirm, _applyMs })
            c.Enabled = false;
        _globalToggle.Enabled = _globalOn;   // may only be turned off
        _tickToggle.Enabled = _tickDisabled; // may only be turned off (re-enable dynamic tick)
    }

    private void RefreshLive()
    {
        var info = TimerInfo.Query();
        _sysInfo.Text =
            $"Current resolution: {info.CurrentMs:0.0000} ms    Supported: {info.FinestMs:0.0000} – {info.CoarsestMs:0.000} ms\n" +
            $"Holder: {(Holder.IsRunning ? "running" : "not running")}    " +
            $"Logon task: {(_taskInstalled ? "installed" : "not installed")}    " +
            $"GlobalTimerResolutionRequests: {(_globalOn ? "on" : "off")}{(SystemConfig.IsWindows11 ? "" : " (not needed before Win11)")}    " +
            $"Dynamic tick: {(_tickDisabled ? "disabled" : "enabled")}";
    }

    private void SetBusy(bool busy)
    {
        foreach (var c in new Control[] { _run, _apply, _install, _uninstall, _stopHolder, _globalToggle, _tickToggle, _export,
                     _start, _end, _coarse, _fine, _samples, _confirm })
            c.Enabled = !busy;
        _cancel.Enabled = busy;
        if (!busy)
        {
            _uninstall.Enabled = _taskInstalled;
            ApplyBuildRestrictions(); // re-enabling everything above must not undo an expiry
        }
    }

    private void SetStatus(string text) => _status.Text = text;

    // ---------- UI helpers ----------

    private void SetupGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = Color.White;

        void Col(string name, Type type, string? format = null)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = name, ValueType = type, SortMode = DataGridViewColumnSortMode.Automatic };
            if (format is not null) c.DefaultCellStyle.Format = format;
            _grid.Columns.Add(c);
        }
        Col("Phase", typeof(string));
        Col("Requested ms", typeof(double), "0.0000");
        Col("Actual ms", typeof(double), "0.0000");
        Col("Mean Δ ms", typeof(double), "0.0000");
        Col("Stdev ms", typeof(double), "0.0000");
        Col("P99 Δ ms", typeof(double), "0.0000");
        Col("Max Δ ms", typeof(double), "0.0000");
        Col("Score", typeof(double), "0.0000");
        Col("Samples", typeof(int));
    }

    private static NumericUpDown Num(decimal min, decimal max, decimal value, int decimals, decimal inc) =>
        new() { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Increment = inc, Width = 80 };

    private static Button Btn(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        b.Click += onClick;
        return b;
    }

    private static Label Lbl(string text) =>
        new() { Text = text, AutoSize = true, Padding = new Padding(6, 7, 0, 0) };

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        f.Controls.AddRange(controls);
        return f;
    }
}
