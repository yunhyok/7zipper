using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;

internal static class Desktop
{
    internal const int LeftButton = 1, RightButton = 2, Escape = 27;
    internal static bool Swapped => GetSystemMetrics(23) != 0; // SM_SWAPBUTTON

    internal static bool Down(int key) => GetAsyncKeyState(key) < 0;

    internal static void Move(Point point)
    {
        if (!SetCursorPos((int)point.X, (int)point.Y)) throw new Win32Exception();
    }

    internal static nint At(Point point) => GetAncestor(WindowFromPoint(new((int)point.X, (int)point.Y)), 2); // GA_ROOT

    internal static void Mouse(bool right, bool down) => Send(new Input
    {
        Data = new() { Mouse = new() { Flags = right ? (down ? 0x8u : 0x10u) : (down ? 0x2u : 0x4u) } }
    });

    internal static void Esc(bool down) => Send(new Input
    {
        Type = 1,
        Data = new() { Key = new() { VirtualKey = Escape, Flags = down ? 0u : 2u } }
    });

    private static void Send(Input input)
    {
        if (SendInput(1, ref input, Marshal.SizeOf<Input>()) != 1) throw new Win32Exception();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ScreenPoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyInput Key;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(ScreenPoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, ref Input input, int size);
    [DllImport("user32.dll")] internal static extern bool SwapMouseButton(bool swap);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("SevenZipper.Shell.dll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    internal static extern int sz_drag_active();
}
