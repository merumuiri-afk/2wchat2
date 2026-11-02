using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TwoWChat.ScreenSharing;

internal sealed class GraphicsWindowCapture : IDisposable
{
    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private byte[] _pixels = [];
    private Windows.Storage.Streams.Buffer? _buffer;
    private volatile bool _closed;
    public GraphicsWindowCapture(IntPtr window, bool monitor = false, bool includeCursor = false)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new IOException("Windows Graphics Capture недоступен на этом компьютере.");
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out var native, out _, out var context));
            try
            {
                var iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in iid, out var dxgi));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                    try { _device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); } finally { Marshal.Release(inspectable); }
                }
                finally { Marshal.Release(dxgi); }
            }
            finally { Marshal.Release(native); if (context != IntPtr.Zero) Marshal.Release(context); }
            const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var hstring));
            try
            {
                var iid = typeof(ICaptureInterop).GUID;
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iid, out var pointer));
                try
                {
                    var factory = (ICaptureInterop)Marshal.GetObjectForIUnknown(pointer);
                    try
                    {
                        var itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
                        IntPtr item;
                        Marshal.ThrowExceptionForHR(monitor ? factory.CreateForMonitor(window, ref itemId, out item) : factory.CreateForWindow(window, ref itemId, out item));
                        try { _item = WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(item); } finally { Marshal.Release(item); }
                    }
                    finally { Marshal.ReleaseComObject(factory); }
                }
                finally { Marshal.Release(pointer); }
            }
            finally { WindowsDeleteString(hstring); }
            _item.Closed += OnClosed;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
            _session = _pool.CreateCaptureSession(_item); _session.IsCursorCaptureEnabled = includeCursor; _session.StartCapture();
        }
        catch { Dispose(); throw; }
    }
    public async Task<Bitmap> ReadAsync(CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_closed) throw new IOException("Выбранное окно закрыто.");
            using var frame = LatestFrame();
            if (frame == null)
            {
                if (Environment.TickCount64 > deadline) throw new IOException("Окно не передаёт кадры. Проверьте, что оно не свёрнуто.");
                await Task.Delay(10, ct); continue;
            }
            var size = frame.ContentSize;
            if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > 40000000) throw new IOException("Недопустимый размер окна.");
            using var software = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            var length = checked(software.PixelWidth * software.PixelHeight * 4);
            if (_pixels.Length != length) { _pixels = new byte[length]; _buffer = new Windows.Storage.Streams.Buffer((uint)length); }
            software.CopyToBuffer(_buffer!);
            using (var reader = DataReader.FromBuffer(_buffer!)) reader.ReadBytes(_pixels);
            var bitmap = new Bitmap(software.PixelWidth, software.PixelHeight, PixelFormat.Format32bppArgb);
            var locked = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(_pixels, 0, locked.Scan0, length); } finally { bitmap.UnlockBits(locked); }
            if (size.Width != software.PixelWidth || size.Height != software.PixelHeight)
            {
                bitmap.Dispose(); frame.Dispose(); _pool!.Recreate(_device!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size); continue;
            }
            return bitmap;
        }
    }
    private Direct3D11CaptureFrame? LatestFrame()
    {
        var frame = _pool!.TryGetNextFrame();
        for (var i = 0; i < 2; i++) { var newer = _pool.TryGetNextFrame(); if (newer == null) break; frame?.Dispose(); frame = newer; }
        return frame;
    }
    private void OnClosed(GraphicsCaptureItem sender, object args) => _closed = true;
    public void Dispose() { if (_item != null) _item.Closed -= OnClosed; _session?.Dispose(); _session = null; _pool?.Dispose(); _pool = null; _device?.Dispose(); _device = null; _item = null; }
    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, uint type, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk, out IntPtr device, out uint level, out IntPtr context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr device);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out IntPtr result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr result);
}
