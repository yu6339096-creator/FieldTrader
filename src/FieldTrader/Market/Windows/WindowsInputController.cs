using System.Runtime.InteropServices;
using FieldTrader.Navigation;

namespace FieldTrader.Market;

/// <summary>
/// Native adapter for an explicitly supplied LOCAL TEST WINDOW owned by this process.
/// No game arming API is exposed in the Mac-stage implementation. Invoke via NavigationExecutor.
/// Client coordinates are physical pixels under PerMonitorV2; do not apply DPI twice.
/// </summary>
public sealed class WindowsInputController(IntPtr localTestWindow) : IInputController {
    public bool IsSimulation => false;
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr Extra;
    }
    // INPUT is 40 bytes on x64 / 28 on x86 (mouse is the largest union member).
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public MouseInput Mouse; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);

    private void CheckWindow(CapturedScreen screen) {
        if (localTestWindow == IntPtr.Zero || screen.WindowHandle != localTestWindow.ToInt64()
            || !IsWindow(localTestWindow) || IsIconic(localTestWindow)
            || GameScreenCapture.GetForegroundWindow() != localTestWindow)
            throw new InvalidOperationException("Local test window must be visible and foreground");
        GameScreenCapture.GetWindowThreadProcessId(localTestWindow, out uint pid);
        if (pid != Environment.ProcessId) throw new InvalidOperationException("Only a local test window owned by FieldTrader is allowed");
        var origin = new GameScreenCapture.Point();
        if (!GameScreenCapture.GetClientRect(localTestWindow, out var rect)
            || !GameScreenCapture.ClientToScreen(localTestWindow, ref origin)
            || origin.X != screen.ScreenX || origin.Y != screen.ScreenY
            || rect.Right - rect.Left != screen.Width || rect.Bottom - rect.Top != screen.Height)
            throw new InvalidOperationException("Client geometry changed; recapture required");
    }
    public Task ExecuteAsync(PlannedAction action, CapturedScreen screen, CancellationToken token) {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!NavigationInputGate.IsOwnedByCurrentFlow) throw new InvalidOperationException("Input requires exclusive NavigationExecutor ownership");
        token.ThrowIfCancellationRequested(); NavigationSafety.Check(action.Step, action.Target);
        if (!screen.Live || screen.Frame.Id != action.Target.FrameId || screen.ContentHash != action.Target.ContentHash
            || screen.Frame.CapturedAt != action.Target.CapturedAt || DateTimeOffset.UtcNow < screen.Frame.CapturedAt
            || DateTimeOffset.UtcNow - screen.Frame.CapturedAt > TimeSpan.FromSeconds(5)
            || action.Target.Click != CoordinateTransform.ToScreen(action.Target.Bounds, screen))
            throw new InvalidOperationException("Invalid/stale target evidence");
        var dpi = GameScreenCapture.SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (dpi == IntPtr.Zero) throw new InvalidOperationException("Cannot establish physical pixel coordinates");
        try {
            CheckWindow(screen); token.ThrowIfCancellationRequested();
            if (!SetCursorPos(action.Target.Click.X, action.Target.Click.Y)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetCursorPos failed");
            CheckWindow(screen); token.ThrowIfCancellationRequested();
            if (action.Step.Operation == InputOperation.Move) return Task.CompletedTask;
            if (action.Step.Operation == InputOperation.Scroll) {
                var wheel = new[] { new NativeInput { Mouse = new MouseInput { Flags = 0x0800, Data = unchecked((uint)action.Step.ScrollDelta) } } };
                if (SendInput(1, wheel, Marshal.SizeOf<NativeInput>()) != 1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Wheel input failed");
            } else {
                // Down/up is a single non-interruptible boundary; cancellation must never leave a held button.
                var click = new[] { new NativeInput { Mouse = new MouseInput { Flags = 0x0002 } },
                    new NativeInput { Mouse = new MouseInput { Flags = 0x0004 } } };
                if (SendInput(2, click, Marshal.SizeOf<NativeInput>()) != 2) {
                    SendInput(1, [new NativeInput { Mouse = new MouseInput { Flags = 0x0004 } }], Marshal.SizeOf<NativeInput>());
                    throw new InvalidOperationException("Partial input; stop and inspect local test window");
                }
            }
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        } finally { GameScreenCapture.SetThreadDpiAwarenessContext(dpi); }
    }
}
