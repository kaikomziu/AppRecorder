using System.Diagnostics;

namespace AppRecorder;

internal sealed class MainForm : Form
{
    readonly ComboBox _windows = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Button _refresh = new() { Text = "更新", AutoSize = true };
    readonly CheckBox _cursor = new() { Text = "マウスカーソルも録画", Checked = true, AutoSize = true };
    readonly CheckBox _audio = new() { Text = "そのアプリの音を録音", Checked = true, AutoSize = true };
    readonly NumericUpDown _fps = new() { Minimum = 10, Maximum = 60, Value = 30, Width = 70 };
    readonly TextBox _outDir = new() { Dock = DockStyle.Fill };
    readonly Button _browse = new() { Text = "参照...", AutoSize = true };
    readonly Button _openDir = new() { Text = "フォルダを開く", AutoSize = true };
    readonly Button _record = new() { Text = "● 録画開始", Dock = DockStyle.Fill, Height = 48, Font = new Font("Yu Gothic UI", 13f, FontStyle.Bold) };
    readonly Label _status = new() { Text = "待機中", AutoSize = true, Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };

    Recorder? _recorder;
    bool _busy;

    public MainForm()
    {
        Text = "AppRecorder - アプリ単位の録画";
        Font = new Font("Yu Gothic UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 320);
        Size = new Size(640, 330);

        _outDir.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "AppRecorder");

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 3, RowCount = 6 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        grid.Controls.Add(Caption("録画するウィンドウ"), 0, 0);
        grid.Controls.Add(_windows, 1, 0);
        grid.Controls.Add(_refresh, 2, 0);

        var opts = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        opts.Controls.Add(_cursor);
        opts.Controls.Add(_audio);
        opts.Controls.Add(new Label { Text = "FPS", AutoSize = true, Margin = new Padding(12, 6, 3, 0) });
        opts.Controls.Add(_fps);
        grid.Controls.Add(Caption("オプション"), 0, 1);
        grid.Controls.Add(opts, 1, 1);
        grid.SetColumnSpan(opts, 2);

        grid.Controls.Add(Caption("保存先"), 0, 2);
        grid.Controls.Add(_outDir, 1, 2);
        var dirButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        dirButtons.Controls.Add(_browse);
        dirButtons.Controls.Add(_openDir);
        grid.Controls.Add(dirButtons, 2, 2);

        grid.Controls.Add(_record, 0, 3);
        grid.SetColumnSpan(_record, 3);
        grid.Controls.Add(_status, 0, 4);
        grid.SetColumnSpan(_status, 3);
        Controls.Add(grid);

        _refresh.Click += (_, _) => RefreshWindows();
        _browse.Click += (_, _) => BrowseOutput();
        _openDir.Click += (_, _) => OpenOutput();
        _record.Click += async (_, _) => await ToggleRecord();
        _timer.Tick += (_, _) => UpdateStatus();

        RefreshWindows();
    }

    static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) };

    void RefreshWindows()
    {
        var prev = _windows.SelectedItem as WindowInfo;
        _windows.BeginUpdate();
        _windows.Items.Clear();
        foreach (var w in WindowList.Enumerate()) _windows.Items.Add(w);
        _windows.EndUpdate();
        if (prev != null)
        {
            var same = _windows.Items.Cast<WindowInfo>().FirstOrDefault(w => w.Hwnd == prev.Hwnd);
            if (same != null) _windows.SelectedItem = same;
        }
        if (_windows.SelectedIndex < 0 && _windows.Items.Count > 0) _windows.SelectedIndex = 0;
    }

    void BrowseOutput()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = _outDir.Text, UseDescriptionForTitle = true, Description = "保存先フォルダ" };
        if (dlg.ShowDialog(this) == DialogResult.OK) _outDir.Text = dlg.SelectedPath;
    }

    void OpenOutput()
    {
        Directory.CreateDirectory(_outDir.Text);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_outDir.Text}\"") { UseShellExecute = true });
    }

    async Task ToggleRecord()
    {
        if (_busy) return;
        if (_recorder is { IsRecording: true }) await StopRecord();
        else await StartRecord();
    }

    async Task StartRecord()
    {
        if (_windows.SelectedItem is not WindowInfo target)
        {
            MessageBox.Show(this, "録画するウィンドウを選んでください。", Text);
            return;
        }
        if (!WindowList.IsAlive(target.Hwnd))
        {
            MessageBox.Show(this, "そのウィンドウは閉じられています。一覧を更新してください。", Text);
            RefreshWindows();
            return;
        }
        var ffmpeg = Recorder.FindFfmpeg() ?? await InstallFfmpeg();
        if (ffmpeg == null) return;

        try
        {
            _recorder = new Recorder(ffmpeg);
            _recorder.TargetClosed += () => BeginInvoke(async () =>
            {
                if (_recorder is { IsRecording: true }) await StopRecord();
            });
            _recorder.Start(target, new RecordOptions((int)_fps.Value, _cursor.Checked, _audio.Checked, _outDir.Text));
        }
        catch (Exception ex)
        {
            _recorder = null;
            MessageBox.Show(this, "録画を開始できませんでした。\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        SetInputsEnabled(false);
        _record.Text = "■ 停止";
        _record.BackColor = Color.FromArgb(220, 60, 60);
        _record.ForeColor = Color.White;
        _timer.Start();
        UpdateStatus();
    }

    // ffmpeg が無いときに winget で入れる。入らなければ null
    async Task<string?> InstallFfmpeg()
    {
        var answer = MessageBox.Show(this,
            "録画の保存に必要な ffmpeg が見つかりません。\n\n" +
            "winget で ffmpeg（Gyan.FFmpeg）をインストールしますか？\n" +
            "（数分かかることがあります）", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            MessageBox.Show(this, "ffmpeg.exe を AppRecorder.exe と同じフォルダに置いても使えます。", Text);
            return null;
        }

        _busy = true;
        _record.Enabled = false;
        _status.Text = "ffmpeg をインストールしています...";
        try
        {
            var psi = new ProcessStartInfo("winget")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "install", "--id", "Gyan.FFmpeg", "-e", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "winget を実行できませんでした。\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            _record.Enabled = true;
        }

        var found = Recorder.FindFfmpeg();
        _status.Text = found != null ? "ffmpeg を準備しました" : "ffmpeg をインストールできませんでした";
        if (found == null)
            MessageBox.Show(this, "ffmpeg をインストールできませんでした。\nffmpeg.exe を AppRecorder.exe と同じフォルダに置いてください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return found;
    }

    async Task StopRecord()
    {
        if (_recorder == null) return;
        _busy = true;
        _timer.Stop();
        _record.Enabled = false;
        _record.Text = "保存中...";
        _status.Text = "MP4 に書き出しています...";
        try
        {
            string path = await _recorder.StopAsync();
            _status.Text = "保存しました: " + path;
        }
        catch (PartialSuccessException ex)
        {
            _status.Text = "保存しました（音声なし）: " + ex.OutputPath;
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            _status.Text = "保存に失敗しました";
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _recorder = null;
            _busy = false;
            _record.Enabled = true;
            _record.Text = "● 録画開始";
            _record.UseVisualStyleBackColor = true;
            _record.ForeColor = SystemColors.ControlText;
            SetInputsEnabled(true);
        }
    }

    void SetInputsEnabled(bool on)
    {
        foreach (Control c in new Control[] { _windows, _refresh, _cursor, _audio, _fps, _outDir, _browse }) c.Enabled = on;
    }

    void UpdateStatus()
    {
        if (_recorder == null) return;
        var t = _recorder.Elapsed;
        _status.Text = $"録画中  {(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_recorder is { IsRecording: true } || _busy)
        {
            e.Cancel = true;
            if (!_busy && MessageBox.Show(this, "録画中です。停止して保存してから閉じますか？", Text, MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                await StopRecord();
                Close();
            }
            return;
        }
        base.OnFormClosing(e);
    }
}
