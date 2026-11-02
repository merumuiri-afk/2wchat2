using System.Runtime.InteropServices;
using System.Text;

namespace TwoWChat.ScreenSharing;

public sealed record WindowChoice(IntPtr Handle, uint ProcessId, Rectangle Bounds, string Title);

public static class WindowCapture
{
    public static WindowChoice[] List()
    {
        var windows = new List<WindowChoice>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle) || handle == GetShellWindow() || handle == GetDesktopWindow()) return true;
            var title = new StringBuilder(512); GetWindowText(handle, title, title.Capacity);
            if (title.Length == 0 || !GetWindowRect(handle, out var rect)) return true;
            GetWindowThreadProcessId(handle, out var pid);
            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (bounds.Width > 0 && bounds.Height > 0) windows.Add(new(handle, pid, bounds, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return windows.OrderBy(w => w.Title).ToArray();
    }
    public static Bitmap Capture(IntPtr handle, uint processId)
    {
        GetWindowThreadProcessId(handle, out var current);
        if (current != processId || !IsWindow(handle) || IsIconic(handle) || !IsWindowVisible(handle) || !GetWindowRect(handle, out var rect))
            throw new IOException("Выбранное окно закрыто, скрыто или свёрнуто. Выберите окно заново.");
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height > 40000000) throw new IOException("Размер окна не поддерживается.");
        var bitmap = new Bitmap(width, height);
        try
        {
            using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.Black);
            var hdc = graphics.GetHdc();
            try
            {
                // Render only the selected window, with a timeout. Never substitute desktop pixels.
                if (SendMessageTimeout(handle, 0x0317, hdc, new IntPtr(0x1e), 2 | 0x20, 500, out _) == IntPtr.Zero)
                    throw new IOException("Окно не отвечает на захват. Попробуйте другое приложение.");
            }
            finally { graphics.ReleaseHdc(hdc); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
    private delegate bool Callback(IntPtr handle, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
