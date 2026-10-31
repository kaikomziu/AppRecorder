using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AppRecorder;

internal sealed record RecordOptions(int Fps, bool CaptureCursor, bool CaptureAudio, string OutputDir);

// 映像（ffmpeg へパイプ）と音声（WAV）を同時に録り、停止時に MP4 へ合成する
internal sealed class Recorder
{
    readonly string _ffmpeg;
    readonly Stopwatch _clock = new();
    WindowVideoCapture? _video;
    ProcessAudioCapture? _audio;
    Process? _encoder;
    Thread? _writer;
    volatile bool _stopVideo;
    long _framesWritten;
    Exception? _writerError;
    readonly StringBuilder _ffmpegLog = new();

    string _tmpVideo = "", _tmpAudio = "", _outPath = "";
    RecordOptions _opt = new(30, true, true, "");

    public event Action? TargetClosed;
    public TimeSpan Elapsed => _clock.Elapsed;
    public bool IsRecording { get; private set; }

    public Recorder(string ffmpegPath) => _ffmpeg = ffmpegPath;

    public void Start(WindowInfo target, RecordOptions opt)
    {
        _opt = opt;
        Directory.CreateDirectory(opt.OutputDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseName = $"{Sanitize(target.ProcessName)}_{stamp}";
        _outPath = Path.Combine(opt.OutputDir, baseName + ".mp4");
        string tmpDir = Path.Combine(Path.GetTempPath(), "AppRecorder");
        Directory.CreateDirectory(tmpDir);
        _tmpVideo = Path.Combine(tmpDir, baseName + ".video.mp4");
        _tmpAudio = Path.Combine(tmpDir, baseName + ".audio.wav");

        _video = new WindowVideoCapture(target.Hwnd, opt.CaptureCursor);
        _video.Closed += () => TargetClosed?.Invoke();

        _encoder = StartEncoder(_video.Width, _video.Height, opt.Fps);

        _framesWritten = 0;
        _stopVideo = false;
        _writerError = null;
        _clock.Restart();
        _video.Start();

        if (opt.CaptureAudio)
        {
            _audio = new ProcessAudioCapture(target.ProcessId, _tmpAudio, _clock);
            try { _audio.Start(); }
            catch { Abort(); throw; }
        }

        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "VideoWriter", Priority = ThreadPriority.AboveNormal };
        _writer.Start();
        IsRecording = true;
    }

    public async Task<string> StopAsync()
    {
        IsRecording = false;
        return await Task.Run(() =>
        {
            _stopVideo = true;
            _writer?.Join();
            double duration = _framesWritten / (double)_opt.Fps;
            _audio?.Stop(duration);
            _clock.Stop();
            _video?.Dispose();

            try { _encoder!.StandardInput.BaseStream.Close(); } catch { }
            _encoder!.WaitForExit();
            if (_encoder.ExitCode != 0 || !File.Exists(_tmpVideo))
                throw new InvalidOperationException("映像のエンコードに失敗しました。\n" + _ffmpegLog);
            if (_writerError != null)
                throw new InvalidOperationException("映像の書き込み中にエラー: " + _writerError.Message);

            bool hasAudio = _audio != null && _audio.Error == null && File.Exists(_tmpAudio);
            if (hasAudio)
            {
                RunFfmpeg("-y", "-loglevel", "error",
                    "-i", _tmpVideo, "-i", _tmpAudio,
                    "-map", "0:v:0", "-map", "1:a:0",
                    "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                    "-movflags", "+faststart", _outPath);
            }
            else
            {
                File.Move(_tmpVideo, _outPath, overwrite: true);
            }

            TryDelete(_tmpVideo);
            TryDelete(_tmpAudio);

            if (_audio?.Error != null)
                throw new PartialSuccessException(_outPath, "映像は保存しましたが、音声の録音でエラーが起きました: " + _audio.Error.Message);
            return _outPath;
        });
    }

    void Abort()
    {
        _stopVideo = true;
        _video?.Dispose();
        try { _encoder?.Kill(); } catch { }
        TryDelete(_tmpVideo);
        TryDelete(_tmpAudio);
    }

    // 固定 FPS で最新フレームを ffmpeg に送る。遅れたら同じフレームを重ねて尺を合わせる
    void WriterLoop()
    {
        timeBeginPeriod(1);
        try
        {
            var buf = new byte[_video!.FrameBytes];
            var stdin = _encoder!.StandardInput.BaseStream;
            int fps = _opt.Fps;
            while (!_stopVideo)
            {
                double t = _clock.Elapsed.TotalSeconds;
                long due = (long)(t * fps) + 1;
                if (_framesWritten >= due)
                {
                    int waitMs = (int)((_framesWritten / (double)fps - t) * 1000);
                    Thread.Sleep(Math.Max(1, waitMs));
                    continue;
                }
                _video.CopyLatest(buf);
                while (_framesWritten < due)
                {
                    stdin.Write(buf, 0, buf.Length);
                    _framesWritten++;
                }
            }
        }
        catch (Exception ex)
        {
            _writerError = ex;
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    Process StartEncoder(int w, int h, int fps)
    {
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "-y", "-loglevel", "error", "-nostats",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{w}x{h}", "-framerate", fps.ToString(), "-i", "-",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", _tmpVideo,
        }) psi.ArgumentList.Add(a);

        _ffmpegLog.Clear();
        var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg を起動できませんでした");
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_ffmpegLog) _ffmpegLog.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        return p;
    }

    void RunFfmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("音声と映像の合成に失敗しました。\n" + err);
    }

    public static string? FindFfmpeg()
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), "ffmpeg.exe"));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"));

        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }

        // winget の PATH 反映前でも見つけられるように、パッケージフォルダも探す
        string pkgs = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(pkgs))
        {
            foreach (var d in Directory.GetDirectories(pkgs, "*FFmpeg*"))
            {
                var hit = Directory.GetFiles(d, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
        }
        return null;
    }

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "record" : s;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
}

internal sealed class PartialSuccessException(string path, string message) : Exception(message)
{
    public string OutputPath { get; } = path;
}
