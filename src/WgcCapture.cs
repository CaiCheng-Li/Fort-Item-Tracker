using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace FortItemTracker;

/// <summary>
/// Windows Graphics Capture of one window or monitor. Frames stay on the GPU: the search area is copied
/// out of each frame and shrunk there, and only the small copy comes back to memory for the pre-check.
/// </summary>
sealed unsafe class WgcCapture : IDisposable
{
    const DirectXPixelFormat Format = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    record struct Staging(IntPtr Texture, int W, int H);

    readonly object sync = new();
    readonly int shrinkTo;
    IntPtr device, context;  // ID3D11Device*, ID3D11DeviceContext*
    IDirect3DDevice? winrtDevice;
    GraphicsCaptureItem? item;
    Direct3D11CaptureFramePool? pool;
    GraphicsCaptureSession? session;
    Direct3D11CaptureFrame? latest;
    SizeInt32 poolSize;
    IntPtr regionTex, regionSrv;  // the search area, with mip levels for shrinking
    int regionW, regionH, regionLevels;
    Staging smallStaging, fullStaging;
    Image32? smallImage;
    bool disposed;

    public IntPtr Key { get; }
    public bool IsWindow { get; }
    public bool Closed { get; private set; }

    /// <summary>When the last frame arrived (Clock.Mono); starts at creation time.</summary>
    public double LastFrameAt { get; private set; } = Clock.Mono;

    public static WgcCapture ForWindow(IntPtr hwnd, TimeSpan minInterval, int shrinkTo) => new(hwnd, true, minInterval, shrinkTo);
    public static WgcCapture ForMonitor(IntPtr hmon, TimeSpan minInterval, int shrinkTo) => new(hmon, false, minInterval, shrinkTo);

    WgcCapture(IntPtr key, bool window, TimeSpan minInterval, int shrinkTo)
    {
        Key = key;
        this.shrinkTo = shrinkTo;
        IsWindow = window;
        try
        {
            (device, context) = D3D.CreateDevice();
            winrtDevice = D3D.ToWinRT(device);
            item = D3D.CreateItem(key, window);
            item.Closed += (_, _) => Closed = true;
            poolSize = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, Format, 2, poolSize);
            pool.FrameArrived += OnFrameArrived;
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) && D3D.BorderlessAllowed())
            {
                try { session.IsBorderRequired = false; } catch (Exception) { }  // no yellow frame round the game
            }
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            {
                try { session.MinUpdateInterval = minInterval; } catch (Exception) { }  // no need for every game frame
            }
            session.StartCapture();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (sync)
        {
            if (disposed) return;
            Direct3D11CaptureFrame? frame;
            while ((frame = sender.TryGetNextFrame()) != null)
            {
                latest?.Dispose();
                latest = frame;
            }
            LastFrameAt = Clock.Mono;
        }
    }

    /// <summary>
    /// Copies the region (in frame pixels) of the newest frame into a texture on the GPU. False when no
    /// new frame arrived since the last call.
    /// </summary>
    public bool CaptureRegion(RECT region)
    {
        Direct3D11CaptureFrame? frame;
        lock (sync)
        {
            frame = latest;
            latest = null;
        }
        if (frame == null) return false;
        using (frame)
        {
            var size = frame.ContentSize;
            if (size.Width != poolSize.Width || size.Height != poolSize.Height)
            {
                lock (sync)
                {
                    poolSize = size;
                    pool!.Recreate(winrtDevice, Format, 2, size);
                }
            }
            IntPtr tex = D3D.TextureFromSurface(frame.Surface);
            try
            {
                var desc = D3D.GetDesc(tex);
                var r = region.Intersect(new RECT(0, 0,
                    Math.Min(size.Width, (int)desc.Width), Math.Min(size.Height, (int)desc.Height)));
                if (r.Width < 1 || r.Height < 1) return false;
                EnsureRegionTexture(r.Width, r.Height);
                var box = new D3D.Box { Left = (uint)r.Left, Top = (uint)r.Top, Front = 0, Right = (uint)r.Right, Bottom = (uint)r.Bottom, Back = 1 };
                D3D.CopySubresourceRegion(context, regionTex, 0, tex, 0, &box);
                return true;
            }
            finally
            {
                Marshal.Release(tex);
            }
        }
    }

    /// <summary>
    /// The captured region halved on the GPU until just before it would drop below <see cref="shrinkTo"/>
    /// pixels tall, so only a small image comes back to memory. Reuses its buffer between calls.
    /// </summary>
    public Image32 ReadShrunk()
    {
        int level = regionLevels - 1;
        if (level > 0) D3D.GenerateMips(context, regionSrv);
        Read(level, Math.Max(1, regionW >> level), Math.Max(1, regionH >> level), ref smallStaging, ref smallImage);
        return smallImage!;
    }

    /// <summary>The captured region at full size (read only when the pre-check passes).</summary>
    public Image32 ReadFull()
    {
        Image32? img = null;
        Read(0, regionW, regionH, ref fullStaging, ref img);
        return img!;
    }

    void Read(int level, int w, int h, ref Staging staging, ref Image32? img)
    {
        if (staging.Texture == IntPtr.Zero || staging.W != w || staging.H != h)
        {
            if (staging.Texture != IntPtr.Zero) Marshal.Release(staging.Texture);
            staging = new Staging(IntPtr.Zero, 0, 0);
            staging = new Staging(D3D.CreateStaging(device, w, h), w, h);
        }
        D3D.CopySubresourceRegion(context, staging.Texture, 0, regionTex, (uint)level, null);
        if (img == null || img.Width != w || img.Height != h) img = new Image32(w, h);
        var mapped = D3D.Map(context, staging.Texture);
        try
        {
            fixed (byte* dst = img.Px)
            {
                int row = w * 4;
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy((byte*)mapped.Data + (long)y * mapped.RowPitch, dst + (long)y * row, row, row);
            }
        }
        finally
        {
            D3D.Unmap(context, staging.Texture);
        }
    }

    void EnsureRegionTexture(int w, int h)
    {
        if (regionTex != IntPtr.Zero && regionW == w && regionH == h) return;
        ReleaseRegion();
        int levels = 1;
        while ((h >> levels) >= shrinkTo) levels++;
        regionTex = D3D.CreateRegionTexture(device, w, h, levels);
        regionSrv = D3D.CreateShaderResourceView(device, regionTex);
        (regionW, regionH, regionLevels) = (w, h, levels);
    }

    void ReleaseRegion()
    {
        foreach (var p in new[] { regionSrv, regionTex, smallStaging.Texture, fullStaging.Texture })
            if (p != IntPtr.Zero) Marshal.Release(p);
        regionSrv = regionTex = IntPtr.Zero;
        smallStaging = fullStaging = default;
        smallImage = null;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            latest?.Dispose();
            latest = null;
        }
        try { session?.Dispose(); } catch (Exception) { }
        try { pool?.Dispose(); } catch (Exception) { }
        try { winrtDevice?.Dispose(); } catch (Exception) { }
        ReleaseRegion();
        if (context != IntPtr.Zero) Marshal.Release(context);
        if (device != IntPtr.Zero) Marshal.Release(device);
        context = device = IntPtr.Zero;
    }
}

/// <summary>The few Direct3D 11 calls capture needs, through the COM vtables.</summary>
static unsafe class D3D
{
    static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
    static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    static readonly Guid IID_IGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint numLevels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [StructLayout(LayoutKind.Sequential)]
    public struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Box
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mapped
    {
        public IntPtr Data;
        public uint RowPitch, DepthPitch;
    }

    static IntPtr* Vtbl(IntPtr p) => *(IntPtr**)p;

    static int QueryInterface(IntPtr p, Guid iid, out IntPtr result)
    {
        IntPtr r;
        int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Vtbl(p)[0])(p, &iid, &r);
        result = r;
        return hr;
    }

    public static (IntPtr Device, IntPtr Context) CreateDevice()
    {
        const uint BgraSupport = 0x20;
        int hr = D3D11CreateDevice(IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, 7, out var dev, out _, out var ctx);
        if (hr < 0)
            hr = D3D11CreateDevice(IntPtr.Zero, 5 /* WARP */, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, 7, out dev, out _, out ctx);
        Marshal.ThrowExceptionForHR(hr);
        // Capture callbacks and Grab run on different threads.
        if (QueryInterface(dev, IID_ID3D10Multithread, out var mt) >= 0)
        {
            ((delegate* unmanaged<IntPtr, int, int>)Vtbl(mt)[5])(mt, 1);  // SetMultithreadProtected(TRUE)
            Marshal.Release(mt);
        }
        return (dev, ctx);
    }

    public static IDirect3DDevice ToWinRT(IntPtr device)
    {
        Marshal.ThrowExceptionForHR(QueryInterface(device, IID_IDXGIDevice, out var dxgi));
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
            try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
            finally { Marshal.Release(inspectable); }
        }
        finally
        {
            Marshal.Release(dxgi);
        }
    }

    public static GraphicsCaptureItem CreateItem(IntPtr handle, bool window)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(Native.WindowsCreateString(className, className.Length, out var hstring));
        IntPtr factory;
        try
        {
            var iid = IID_IGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(Native.RoGetActivationFactory(hstring, ref iid, out factory));
        }
        finally
        {
            Native.WindowsDeleteString(hstring);
        }
        try
        {
            Guid itemIid = IID_IGraphicsCaptureItem;
            IntPtr itemPtr;
            // IGraphicsCaptureItemInterop: CreateForWindow = slot 3, CreateForMonitor = slot 4
            int hr = ((delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, int>)Vtbl(factory)[window ? 3 : 4])(factory, handle, &itemIid, &itemPtr);
            Marshal.ThrowExceptionForHR(hr);
            try { return GraphicsCaptureItem.FromAbi(itemPtr); }
            finally { Marshal.Release(itemPtr); }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    static bool? borderless;

    /// <summary>Asks once whether this app may capture without the yellow border (Windows 11).</summary>
    public static bool BorderlessAllowed()
    {
        if (borderless is bool known) return known;
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            {
                var status = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().GetAwaiter().GetResult();
                borderless = status == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed;
            }
            else
            {
                borderless = false;
            }
        }
        catch (Exception)
        {
            borderless = false;
        }
        return borderless.Value;
    }

    /// <summary>The ID3D11Texture2D behind a capture frame (caller releases it).</summary>
    public static IntPtr TextureFromSurface(IDirect3DSurface surface)
    {
        IntPtr unk = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(QueryInterface(unk, IID_IDirect3DDxgiInterfaceAccess, out var access));
            try
            {
                Guid iid = IID_ID3D11Texture2D;
                IntPtr tex;
                int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Vtbl(access)[3])(access, &iid, &tex);  // GetInterface
                Marshal.ThrowExceptionForHR(hr);
                return tex;
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(unk);
        }
    }

    public static Texture2DDesc GetDesc(IntPtr texture)
    {
        Texture2DDesc desc;
        ((delegate* unmanaged<IntPtr, Texture2DDesc*, void>)Vtbl(texture)[10])(texture, &desc);  // ID3D11Texture2D::GetDesc
        return desc;
    }

    static IntPtr CreateTexture(IntPtr device, Texture2DDesc desc)
    {
        IntPtr tex;
        int hr = ((delegate* unmanaged<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)Vtbl(device)[5])(device, &desc, IntPtr.Zero, &tex);  // CreateTexture2D
        Marshal.ThrowExceptionForHR(hr);
        return tex;
    }

    /// <summary>A texture the CPU can read.</summary>
    public static IntPtr CreateStaging(IntPtr device, int w, int h) => CreateTexture(device, new Texture2DDesc
    {
        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
        Format = 87,               // DXGI_FORMAT_B8G8R8A8_UNORM
        SampleCount = 1,
        Usage = 3,                 // D3D11_USAGE_STAGING
        CpuAccessFlags = 0x20000,  // D3D11_CPU_ACCESS_READ
    });

    /// <summary>A GPU texture with <paramref name="levels"/> mip levels that GenerateMips can fill.</summary>
    public static IntPtr CreateRegionTexture(IntPtr device, int w, int h, int levels) => CreateTexture(device, new Texture2DDesc
    {
        Width = (uint)w, Height = (uint)h, MipLevels = (uint)levels, ArraySize = 1,
        Format = 87,
        SampleCount = 1,
        Usage = 0,                 // D3D11_USAGE_DEFAULT
        BindFlags = 0x8 | 0x20,    // SHADER_RESOURCE | RENDER_TARGET
        MiscFlags = 0x1,           // D3D11_RESOURCE_MISC_GENERATE_MIPS
    });

    public static IntPtr CreateShaderResourceView(IntPtr device, IntPtr resource)
    {
        IntPtr srv;
        int hr = ((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Vtbl(device)[7])(device, resource, null, &srv);  // CreateShaderResourceView
        Marshal.ThrowExceptionForHR(hr);
        return srv;
    }

    public static void GenerateMips(IntPtr context, IntPtr srv) =>
        ((delegate* unmanaged<IntPtr, IntPtr, void>)Vtbl(context)[54])(context, srv);

    public static void CopySubresourceRegion(IntPtr context, IntPtr dst, uint dstSub, IntPtr src, uint srcSub, Box* box) =>
        ((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)Vtbl(context)[46])(context, dst, dstSub, 0, 0, 0, src, srcSub, box);

    public static Mapped Map(IntPtr context, IntPtr resource)
    {
        Mapped m;
        int hr = ((delegate* unmanaged<IntPtr, IntPtr, uint, int, uint, Mapped*, int>)Vtbl(context)[14])(context, resource, 0, 1 /* READ */, 0, &m);
        Marshal.ThrowExceptionForHR(hr);
        return m;
    }

    public static void Unmap(IntPtr context, IntPtr resource) =>
        ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Vtbl(context)[15])(context, resource, 0);
}
