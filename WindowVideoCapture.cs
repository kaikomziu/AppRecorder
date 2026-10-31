using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace AppRecorder;

// Windows.Graphics.Capture で 1 つのウィンドウだけをキャプチャし、
// 最新フレームを BGRA のバイト列として保持する
internal sealed unsafe class WindowVideoCapture : IDisposable
{
    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _context;
    readonly IDirect3DDevice _winrtDevice;
    readonly GraphicsCaptureItem _item;
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;
    readonly object _lock = new();
    readonly byte[] _latest;

    SizeInt32 _poolSize;
    ID3D11Texture2D? _staging;
    int _stagingW, _stagingH;
    bool _disposed;

    // 出力サイズ（録画開始時のウィンドウサイズ。yuv420p のため偶数に丸める）
    public int Width { get; }
    public int Height { get; }
    public int FrameBytes => Width * Height * 4;

    public event Action? Closed;

    public WindowVideoCapture(IntPtr hwnd, bool captureCursor)
    {
        D3D11.D3D11CreateDevice(
            (IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out var device).CheckError();
        _device = device!;
        _context = _device.ImmediateContext;
        _winrtDevice = CreateWinRTDevice(_device);

        _item = CreateItemForWindow(hwnd);
        _item.Closed += (_, _) => Closed?.Invoke();

        _poolSize = _item.Size;
        Width = Math.Max(2, _poolSize.Width & ~1);
        Height = Math.Max(2, _poolSize.Height & ~1);
        _latest = new byte[FrameBytes];

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;

        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = captureCursor;
        // Windows 11 では黄色い枠を消せる
        if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsBorderRequired)))
        {
            try { _session.IsBorderRequired = false; } catch { }
        }
    }

    public void Start() => _session.StartCapture();

    // 最新フレームを dst にコピーする
    public void CopyLatest(byte[] dst)
    {
        lock (_lock) Buffer.BlockCopy(_latest, 0, dst, 0, _latest.Length);
    }

    void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame == null) return;
        var contentSize = frame.ContentSize;

        lock (_lock)
        {
            if (_disposed) return;
            using var tex = GetTexture(frame.Surface);
            var desc = tex.Description;
            EnsureStaging((int)desc.Width, (int)desc.Height);
            _context.CopyResource(_staging!, tex);

            var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int w = Math.Min(Math.Min(contentSize.Width, (int)desc.Width), Width);
                int h = Math.Min(Math.Min(contentSize.Height, (int)desc.Height), Height);
                // ウィンドウが縮んだときは余白を黒にする
                if (w < Width || h < Height) Array.Clear(_latest);
                long pitch = map.RowPitch;
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(map.DataPointer + (nint)(y * pitch), _latest, y * Width * 4, w * 4);
                }
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }
        }

        // ウィンドウサイズが変わったらフレームプールを作り直す
        if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
        {
            _poolSize = contentSize;
            sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
        }
    }

    void EnsureStaging(int w, int h)
    {
        if (_staging != null && _stagingW == w && _stagingH == h) return;
        _staging?.Dispose();
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
        _stagingW = w;
        _stagingH = h;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
        _staging?.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    // ---- 相互運用 ----

    static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstr));
        IntPtr factory;
        try
        {
            var iid = IID_IGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstr, ref iid, out factory));
        }
        finally
        {
            WindowsDeleteString(hstr);
        }

        try
        {
            // IGraphicsCaptureItemInterop::CreateForWindow（vtable 3 番目）
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(*(void***)factory + 3));
            var itemIid = IID_IGraphicsCaptureItem;
            IntPtr itemPtr;
            Marshal.ThrowExceptionForHR(fn(factory, hwnd, &itemIid, &itemPtr));
            try { return GraphicsCaptureItem.FromAbi(itemPtr); }
            finally { Marshal.Release(itemPtr); }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    static IDirect3DDevice CreateWinRTDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var p));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(p); }
        finally { Marshal.Release(p); }
    }

    static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var accessIid = IID_IDirect3DDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, ref accessIid, out var access));
            try
            {
                // IDirect3DDxgiInterfaceAccess::GetInterface（vtable 3 番目）
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(*(void***)access + 3));
                var texIid = IID_ID3D11Texture2D;
                IntPtr texPtr;
                Marshal.ThrowExceptionForHR(fn(access, &texIid, &texPtr));
                return new ID3D11Texture2D(texPtr);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(surfacePtr);
        }
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string src, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
