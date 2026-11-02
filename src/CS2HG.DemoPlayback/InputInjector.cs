using System.Diagnostics;
using CS2HG.Core.Localization;

namespace CS2HG.DemoPlayback;

/// <summary>Sends keyboard input to the CS2 window (SendInput needs the window in the foreground).</summary>
public static class InputInjector
{
    public static bool IsForeground(IntPtr window) => Native.GetForegroundWindow() == window;

    /// <summary>Brings the window to the foreground using the documented workarounds for the foreground lock.</summary>
    public static bool Focus(IntPtr window)
    {
        if (window == IntPtr.Zero || !Native.IsWindow(window)) return false;
        if (Native.IsIconic(window))
        {
            Native.ShowWindow(window, Native.SW_RESTORE);
            Thread.Sleep(800);
        }

        if (IsForeground(window)) return true;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var fg = Native.GetForegroundWindow();
            var fgThread = Native.GetWindowThreadProcessId(fg, out _);
            var myThread = Native.GetCurrentThreadId();
            var attached = fgThread != 0 && fgThread != myThread && Native.AttachThreadInput(myThread, fgThread, true);
            try
            {
                // A synthetic ALT press lifts the foreground lock for the calling process.
                Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
                Native.keybd_event(0x12, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                Native.BringWindowToTop(window);
                Native.SetForegroundWindow(window);
            }
            finally
            {
                if (attached) Native.AttachThreadInput(myThread, fgThread, false);
            }

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 400)
            {
                if (IsForeground(window)) return true;
                Thread.Sleep(20);
            }
        }

        return IsForeground(window);
    }

    public static void TapScanCode(ushort scanCode, int holdMs = 40)
    {
        Send(0, scanCode, Native.KEYEVENTF_SCANCODE);
        Thread.Sleep(holdMs);
        Send(0, scanCode, Native.KEYEVENTF_SCANCODE | Native.KEYEVENTF_KEYUP);
    }

    public static void TypeText(string text)
    {
        foreach (var ch in text)
        {
            Send(0, ch, Native.KEYEVENTF_UNICODE);
            Send(0, ch, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP);
            Thread.Sleep(4);
        }
    }

    public static void PressEnter() => TapScanCode(0x1C);

    private static void Send(ushort vk, ushort scan, uint flags)
    {
        var inputs = new Native.INPUT[1];
        inputs[0].type = Native.INPUT_KEYBOARD;
        inputs[0].ki.wVk = vk;
        inputs[0].ki.wScan = scan;
        inputs[0].ki.dwFlags = flags;
        if (Native.SendInput(1, inputs, 40) == 0)
            throw new InvalidOperationException(L.T("cs2.sendInputFailed"));
    }
}
