using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AppRecorder;

internal sealed record WindowInfo(IntPtr Hwnd, string Title, uint ProcessId, string ProcessName)
{
    public override string ToString() => $"{Title}    [{ProcessName}]";
}

internal static class WindowList
{
    // 録画対象になりうるトップレベルウィンドウを列挙する
    public static List<WindowInfo> Enumerate()
    {
        var result = new List<WindowInfo>();
        uint selfPid = (uint)Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;
            if (IsCloaked(hwnd)) return true;

            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == selfPid) return true;

            string procName;
            try { procName = Process.GetProcessById((int)pid).ProcessName; }
            catch { procName = "?"; }

            result.Add(new WindowInfo(hwnd, sb.ToString(), pid, procName));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    public static bool IsAlive(IntPtr hwnd) => IsWindow(hwnd);

    static bool IsCloaked(IntPtr hwnd)
    {
        // 非表示の UWP ウィンドウなどは DWM で cloak されている
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    const int GW_OWNER = 4;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x00000080;
    const int DWMWA_CLOAKED = 14;

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
