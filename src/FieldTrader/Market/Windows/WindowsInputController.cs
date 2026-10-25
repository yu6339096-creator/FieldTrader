using System.Runtime.InteropServices;
using FieldTrader.Navigation;

namespace FieldTrader.Market;

/// <summary>
/// Standard Windows SendInput adapter for an owned test window or a bounded game transaction.
/// Client coordinates are physical pixels under PerMonitorV2; do not apply DPI twice.
/// </summary>
internal static class LocalNavigationWindows {
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long,bool> Allowed=new();
    internal static void Register(IntPtr hwnd)=>Allowed[hwnd.ToInt64()]=true;
    internal static void Remove(IntPtr hwnd)=>Allowed.TryRemove(hwnd.ToInt64(),out _);
    internal static bool Authorized(IntPtr hwnd)=>Allowed.ContainsKey(hwnd.ToInt64());
}
public sealed class WindowsInputController(IntPtr localTestWindow) : IInputController {
    private bool gameTransaction;
    private Action<string,object>? trace;
    internal static WindowsInputController ForGameTransaction(IntPtr window,Action<string,object>? trace=null)=>new(window){gameTransaction=true,trace=trace};
    public bool IsSimulation => false;
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr Extra;
    }
    // INPUT is 40 bytes on x64 / 28 on x86 (mouse is the largest union member).
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public MouseInput Mouse; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out GameScreenCapture.Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClipCursor(out GameScreenCapture.Rect rect);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool GetUserObjectInformation(IntPtr handle,int index,System.Text.StringBuilder value,uint length,out uint needed);
    private static string DesktopName(IntPtr handle) {
        if(handle==IntPtr.Zero)return "unavailable";
        var name=new System.Text.StringBuilder(256);
        return GetUserObjectInformation(handle,2,name,512,out _)?name.ToString():"error:"+Marshal.GetLastWin32Error();
    }
    // Read-only diagnostics: never switch desktops, alter clipping, or change input permissions.
    private static string CursorDiagnostics(int x,int y) {
        var current=DesktopName(GetThreadDesktop(GetCurrentThreadId()));
        var input=OpenInputDesktop(0,false,1);int openError=Marshal.GetLastWin32Error();
        string inputName;
        try{inputName=input==IntPtr.Zero?"error:"+openError:DesktopName(input);}finally{if(input!=IntPtr.Zero)CloseDesktop(input);}
        bool positionOk=GetCursorPos(out var position);int positionError=Marshal.GetLastWin32Error();
        bool clipOk=GetClipCursor(out var clip);int clipError=Marshal.GetLastWin32Error();
        return $"target=({x},{y}); cursor={(positionOk?$"({position.X},{position.Y})":"error:"+positionError)}; clip={(clipOk?$"({clip.Left},{clip.Top},{clip.Right},{clip.Bottom})":"error:"+clipError)}; threadDesktop={current}; inputDesktop={inputName}";
    }
    internal static string InspectEnvironment() {
        var window=GameScreenCapture.GetForegroundWindow();
        using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
        bool admin=new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        return $"administrator={admin}; foregroundGame={GameScreenCapture.GameForeground(window)}; process64={Environment.Is64BitProcess}; "+CursorDiagnostics(0,0);
    }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);

    private void CheckWindow(CapturedScreen screen) {
        if ((!gameTransaction&&!LocalNavigationWindows.Authorized(localTestWindow))||localTestWindow == IntPtr.Zero || screen.WindowHandle != localTestWindow.ToInt64()
            || !IsWindow(localTestWindow) || IsIconic(localTestWindow)
            || GameScreenCapture.GetForegroundWindow() != localTestWindow)
            throw gameTransaction?new InputNotSentException("游戏窗口已切走、关闭或最小化，尚未发送点击"):new InvalidOperationException("Local test window must be visible and foreground");
        GameScreenCapture.GetWindowThreadProcessId(localTestWindow, out uint pid);
        if(gameTransaction) { if(!GameScreenCapture.GameForeground(localTestWindow))throw new InputNotSentException("当前前台不是预期游戏窗口，尚未发送点击"); }
        else if (pid != Environment.ProcessId) throw new InvalidOperationException("Only a local test window owned by FieldTrader is allowed");
        var origin = new GameScreenCapture.Point();
        if (!GameScreenCapture.GetClientRect(localTestWindow, out var rect)
            || !GameScreenCapture.ClientToScreen(localTestWindow, ref origin)
            || origin.X != screen.ScreenX || origin.Y != screen.ScreenY
            || rect.Right - rect.Left != screen.Width || rect.Bottom - rect.Top != screen.Height)
            throw gameTransaction?new InputNotSentException("游戏窗口位置或大小改变，尚未发送点击；下轮将重新截图"):new InvalidOperationException("Client geometry changed; recapture required");
    }
    public Task ExecuteAsync(PlannedAction action, CapturedScreen screen, CancellationToken token) {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!NavigationInputGate.IsOwnedByCurrentFlow) throw new InvalidOperationException("Input requires exclusive NavigationExecutor ownership");
        token.ThrowIfCancellationRequested();
        if(gameTransaction) {
            if(action.Step.Operation!=InputOperation.Click||action.Target.Category!="trade_submit"||!new[]{"购买","补齐道具","兑换","确定"}.Contains(action.Target.Name))throw new InvalidOperationException("Only bounded buy/exchange submissions are permitted");
        } else NavigationSafety.Check(action.Step, action.Target);
        if (!screen.Live || screen.Frame.Id != action.Target.FrameId || screen.ContentHash != action.Target.ContentHash
            || screen.Frame.CapturedAt != action.Target.CapturedAt || DateTimeOffset.UtcNow < screen.Frame.CapturedAt
            || DateTimeOffset.UtcNow - screen.Frame.CapturedAt > TimeSpan.FromSeconds(5)
            || action.Target.Click != CoordinateTransform.ToScreen(action.Target.Bounds, screen))
            throw new InvalidOperationException("Invalid/stale target evidence");
        var dpi = GameScreenCapture.SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (dpi == IntPtr.Zero) throw new InvalidOperationException("Cannot establish physical pixel coordinates");
        try {
            CheckWindow(screen); token.ThrowIfCancellationRequested();
            int size=Marshal.SizeOf<NativeInput>();
            if(size!=(Environment.Is64BitProcess?40:28))throw new InputNotSentException("Windows鼠标输入结构大小错误，尚未发送点击");
            var point=AbsoluteMouseCoordinates.Normalize(action.Target.Click.X,action.Target.Click.Y,GetSystemMetrics(76),GetSystemMetrics(77),GetSystemMetrics(78),GetSystemMetrics(79));
            trace?.Invoke("MoveRequested",new{Frame=screen.Frame.Id,Target=action.Target.Click,AbsoluteX=point.X,AbsoluteY=point.Y,InputBytes=size});
            uint moved=SendInput(1,[new NativeInput{Mouse=new MouseInput{X=point.X,Y=point.Y,Flags=0x0001|0x8000|0x4000}}],size);
            int moveError=Marshal.GetLastWin32Error();
            trace?.Invoke("MoveReturned",new{Inserted=moved,Win32Error=moveError});
            if(moved!=1)throw new InputNotSentException($"Windows SendInput未接受鼠标移动（{moved}/1，Win32 {moveError}），尚未发送点击；"+CursorDiagnostics(action.Target.Click.X,action.Target.Click.Y));
            // Let the normal Windows input queue process movement, then verify before button-down.
            // Stay on this worker thread: DPI awareness is thread-local and must be restored on it.
            System.Threading.Thread.Sleep(60);token.ThrowIfCancellationRequested();
            if(!GetCursorPos(out var actual)||actual.X!=action.Target.Click.X||actual.Y!=action.Target.Click.Y)
                throw new InputNotSentException("鼠标未到达目标，尚未发送点击；"+CursorDiagnostics(action.Target.Click.X,action.Target.Click.Y));
            trace?.Invoke("MoveVerified",new{actual.X,actual.Y});
            CheckWindow(screen); token.ThrowIfCancellationRequested();
            if(DateTimeOffset.UtcNow-screen.Frame.CapturedAt>TimeSpan.FromSeconds(5))throw new InputNotSentException("移动后截图过期，尚未发送点击");
            if (action.Step.Operation == InputOperation.Move) return Task.CompletedTask;
            if (action.Step.Operation == InputOperation.Scroll) {
                var wheel = new[] { new NativeInput { Mouse = new MouseInput { Flags = 0x0800, Data = unchecked((uint)action.Step.ScrollDelta) } } };
                if (SendInput(1, wheel, Marshal.SizeOf<NativeInput>()) != 1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Wheel input failed");
            } else {
                // Down/up is a single non-interruptible boundary; cancellation must never leave a held button.
                var click = new[] { new NativeInput { Mouse = new MouseInput { Flags = 0x0002 } },
                    new NativeInput { Mouse = new MouseInput { Flags = 0x0004 } } };
                trace?.Invoke("ClickRequested",new{Frame=screen.Frame.Id,Button=action.Target.Name,Target=action.Target.Click});
                uint inserted=SendInput(2,click,size);int clickError=Marshal.GetLastWin32Error();
                trace?.Invoke("ClickReturned",new{Inserted=inserted,Expected=2,Win32Error=clickError});
                if(inserted==0)throw new InputNotSentException($"Windows SendInput未接受点击（0/2，Win32 {clickError}）；不自动重试");
                if (inserted != 2) {
                    uint released=SendInput(1, [new NativeInput { Mouse = new MouseInput { Flags = 0x0004 } }],size);
                    trace?.Invoke("ReleaseAfterPartialInput",new{Inserted=released});
                    throw new InvalidOperationException("鼠标点击仅发送了一部分；停止并保留未决记录，不重复提交");
                }
            }
            // Once down/up has been sent, return it to the coordinator for accurate
            // click accounting. The coordinator observes cancellation before any next action.
            return Task.CompletedTask;
        } finally { GameScreenCapture.SetThreadDpiAwarenessContext(dpi); }
    }
}
