using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AppRecorder;

// プロセス単位のループバック録音（Windows 10 2004 以降）。
// 指定 PID とその子プロセスが鳴らした音だけを WAV に書き出す。
internal sealed class ProcessAudioCapture
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    const int BitsPerSample = 16;
    const int BlockAlign = Channels * BitsPerSample / 8;

    readonly uint _pid;
    readonly string _path;
    readonly Stopwatch _clock;
    readonly ManualResetEventSlim _ready = new();
    Thread? _thread;
    volatile bool _stop;
    double _targetSeconds;
    Exception? _error;

    public Exception? Error => _error;

    public ProcessAudioCapture(uint pid, string wavPath, Stopwatch clock)
    {
        _pid = pid;
        _path = wavPath;
        _clock = clock;
    }

    public void Start()
    {
        // 起動処理は MTA スレッドで行う（UI の STA で待つとデッドロックしうる）
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioCapture" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _ready.Wait();
        if (_error != null) throw new InvalidOperationException("音声キャプチャを開始できませんでした: " + _error.Message, _error);
    }

    // targetSeconds まで無音で埋めてから止める（映像と尺を合わせるため）
    public void Stop(double targetSeconds)
    {
        _targetSeconds = targetSeconds;
        _stop = true;
        _thread?.Join();
    }

    void Run()
    {
        IAudioClient? client = null;
        WavWriter? wav = null;
        try
        {
            client = Activate(_pid);

            var fmt = new WaveFormatEx
            {
                wFormatTag = 1, // PCM
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                nAvgBytesPerSec = SampleRate * BlockAlign,
                nBlockAlign = BlockAlign,
                wBitsPerSample = BitsPerSample,
                cbSize = 0,
            };
            IntPtr pFmt = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
            try
            {
                Marshal.StructureToPtr(fmt, pFmt, false);
                client.Initialize(0 /* SHARED */,
                    AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                    AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                    2_000_000 /* 200ms */, 0, pFmt, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(pFmt);
            }

            using var ev = new AutoResetEvent(false);
            client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
            var capIid = typeof(IAudioCaptureClient).GUID;
            client.GetService(ref capIid, out object capObj);
            var cap = (IAudioCaptureClient)capObj;

            wav = new WavWriter(_path, SampleRate, Channels, BitsPerSample);
            client.Start();
            _ready.Set();

            var buf = new byte[SampleRate * BlockAlign];
            long written = 0;
            while (!_stop)
            {
                ev.WaitOne(20);
                bool got = false;
                while (true)
                {
                    Check(cap.GetNextPacketSize(out uint packet));
                    if (packet == 0) break;
                    Check(cap.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));
                    int bytes = (int)frames * BlockAlign;
                    if (buf.Length < bytes) buf = new byte[bytes];
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(buf, 0, bytes);
                    else Marshal.Copy(data, buf, 0, bytes);
                    Check(cap.ReleaseBuffer(frames));
                    wav.Write(buf, 0, bytes);
                    written += frames;
                    got = true;
                }

                // 対象アプリが無音の間はパケットが来ないので、経過時間に合わせて無音を挿入する
                long expected = (long)(_clock.Elapsed.TotalSeconds * SampleRate);
                if (!got && expected - written > SampleRate / 10)
                {
                    long pad = expected - written - SampleRate / 50;
                    written += WriteSilence(wav, pad);
                }
            }

            client.Stop();
            long target = (long)(_targetSeconds * SampleRate);
            if (target > written) written += WriteSilence(wav, target - written);
        }
        catch (Exception ex)
        {
            _error = ex;
        }
        finally
        {
            wav?.Dispose();
            if (client != null) Marshal.ReleaseComObject(client);
            _ready.Set();
        }
    }

    static long WriteSilence(WavWriter wav, long frames)
    {
        var zero = new byte[4096 * BlockAlign];
        long left = frames;
        while (left > 0)
        {
            int n = (int)Math.Min(left, 4096);
            wav.Write(zero, 0, n * BlockAlign);
            left -= n;
        }
        return frames;
    }

    static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    static IAudioClient Activate(uint pid)
    {
        var p = new AudioClientActivationParams
        {
            ActivationType = 1, // PROCESS_LOOPBACK
            TargetProcessId = pid,
            ProcessLoopbackMode = 0, // INCLUDE_TARGET_PROCESS_TREE
        };
        int pSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr pParams = Marshal.AllocHGlobal(pSize);
        IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(p, pParams, false);
            Marshal.StructureToPtr(new PropVariantBlob { vt = 65 /* VT_BLOB */, cbSize = (uint)pSize, pBlobData = pParams }, pVariant, false);

            var handler = new ActivationHandler();
            var iid = typeof(IAudioClient).GUID;
            ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, ref iid, pVariant, handler, out var op);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("音声デバイスの初期化がタイムアウトしました");
            op.GetActivateResult(out int hr, out object iface);
            Marshal.ThrowExceptionForHR(hr);
            GC.KeepAlive(op);
            return (IAudioClient)iface;
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
        }
    }

    // ---- 相互運用定義 ----

    const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";
    const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    const int AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
    const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariantBlob
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None), ComVisible(true)]
    sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => Done.Set();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        void GetBufferSize(out uint numBufferFrames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
        void GetMixFormat(out IntPtr deviceFormat);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }
}

// 最小限の PCM WAV 書き出し（終了時にヘッダのサイズを書き戻す）
internal sealed class WavWriter : IDisposable
{
    readonly FileStream _fs;
    long _dataBytes;

    public WavWriter(string path, int sampleRate, int channels, int bits)
    {
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        int blockAlign = channels * bits / 8;
        using var w = new BinaryWriter(_fs, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8); w.Write(0);
        w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16);
        w.Write((short)1); w.Write((short)channels);
        w.Write(sampleRate); w.Write(sampleRate * blockAlign);
        w.Write((short)blockAlign); w.Write((short)bits);
        w.Write("data"u8); w.Write(0);
    }

    public void Write(byte[] buf, int offset, int count)
    {
        _fs.Write(buf, offset, count);
        _dataBytes += count;
    }

    public void Dispose()
    {
        using (var w = new BinaryWriter(_fs, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Seek(4, SeekOrigin.Begin); w.Write((int)Math.Min(uint.MaxValue, 36 + _dataBytes));
            w.Seek(40, SeekOrigin.Begin); w.Write((int)Math.Min(uint.MaxValue, _dataBytes));
        }
        _fs.Dispose();
    }
}
