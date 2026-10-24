using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FieldTrader.Market;

namespace FieldTrader.NavigationLab;

internal static class Native {
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr h, ref Point p);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int n);
    [DllImport("user32.dll", SetLastError=true)] internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint op);
}

// Captures the visible desktop pixels of one expressly authorized, same-process HWND.
// No game process and no UI Automation/control metadata is used for target recognition.
internal sealed class LocalScreenSource(IntPtr hwnd, string directory) : IScreenSource {
    internal CapturedScreen? Last;
    public async Task<CapturedScreen> CaptureAsync(CancellationToken token) {
        await Task.Delay(45, token); // Allow actual WPF input/render queues and DWM to settle.
        token.ThrowIfCancellationRequested();
        var previous = Native.SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous == IntPtr.Zero) throw new IOException("PerMonitorV2 unavailable");
        IntPtr dc=IntPtr.Zero, mem=IntPtr.Zero, bitmap=IntPtr.Zero, old=IntPtr.Zero;
        try {
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != Environment.ProcessId || !Native.IsWindow(hwnd) || Native.IsIconic(hwnd)
                || Native.GetForegroundWindow()!=hwnd) throw new IOException($"Local capture requires the authorized visible foreground window (target={hwnd}, foreground={Native.GetForegroundWindow()}, pid={pid}, self={Environment.ProcessId})");
            var p=new Native.Point();
            if (!Native.GetClientRect(hwnd,out var r) || !Native.ClientToScreen(hwnd,ref p)) throw new IOException("Geometry unavailable");
            int w=r.Right-r.Left,h=r.Bottom-r.Top;
            if(w<100||h<100)throw new IOException("Invalid client size");
            dc=Native.GetDC(IntPtr.Zero);mem=Native.CreateCompatibleDC(dc);bitmap=Native.CreateCompatibleBitmap(dc,w,h);
            if(dc==IntPtr.Zero||mem==IntPtr.Zero||bitmap==IntPtr.Zero)throw new IOException("GDI allocation failed");
            old=Native.SelectObject(mem,bitmap);
            if(old==IntPtr.Zero||old==new IntPtr(-1))throw new IOException("SelectObject failed");
            if(!Native.BitBlt(mem,0,0,w,h,dc,p.X,p.Y,0x00CC0020))throw new IOException("BitBlt failed");
            var at=DateTimeOffset.UtcNow;
            var pixels=Imaging.CreateBitmapSourceFromHBitmap(bitmap,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
            var again=new Native.Point();
            if(Native.GetForegroundWindow()!=hwnd||!Native.GetClientRect(hwnd,out var rr)||!Native.ClientToScreen(hwnd,ref again)
                ||r.Right!=rr.Right||r.Bottom!=rr.Bottom||p.X!=again.X||p.Y!=again.Y)throw new IOException("Window changed during capture");
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(pixels));
            using var stream=new MemoryStream();encoder.Save(stream);var bytes=stream.ToArray();
            Directory.CreateDirectory(directory);var id=Guid.NewGuid().ToString("N");var path=Path.Combine(directory,id+".png");
            token.ThrowIfCancellationRequested();File.WriteAllBytes(path,bytes);
            return Last=new(new(id,at,path),Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),w,h,hwnd.ToInt64(),p.X,p.Y,true);
        } finally {
            if(old!=IntPtr.Zero&&old!=new IntPtr(-1))Native.SelectObject(mem,old);
            if(bitmap!=IntPtr.Zero)Native.DeleteObject(bitmap);if(mem!=IntPtr.Zero)Native.DeleteDC(mem);
            if(dc!=IntPtr.Zero)Native.ReleaseDC(IntPtr.Zero,dc);Native.SetThreadDpiAwarenessContext(previous);
        }
    }
}

internal sealed record Marker(string Name, int Color, string? Category);
internal static class Markers {
    internal static readonly List<Marker> All = [
        new("LocalLab",0x3265A8,null), new("Page Home",0x3E7CA1,null),new("Page Detail",0x7F419A,null),new("Page Warehouse",0x215BA9,null),new("Page Contact",0x8A3C20,null),
        new("A",0xA33451,"product_card"),new("B",0x287E64,"product_card"),new("C",0x7A5B24,"product_card"),
        new("D",0x613E91,"product_card"),new("E",0x2A6C9B,"product_card"),new("F",0x956329,"product_card"),
        new("Next",0x466B3D,"market_entry"),new("Shift",0x864856,"market_entry"),new("Popup",0x476A83,"market_entry"),
        new("Close",0x884D26,"secondary_button"),new("Scroll",0x376961,"material_row"),
        new("ListTarget",0x754E87,"product_card"),new("Scrolled",0xB171A1,null),new("Dialog Open",0x9172B2,null)
    ];
    static Markers(){ for(int i=0;i<=64;i++)All.Add(new("Count "+i,0xC02040+i*0x200,null)); }
    internal static Marker Get(string name)=>All.Single(m=>m.Name==name);
    internal static SolidColorBrush Brush(string name){var c=Get(name).Color;return new(Color.FromRgb((byte)(c>>16),(byte)(c>>8),(byte)c));}
}

// FIXTURE detector, deliberately not YOLO/OCR. Reads only persisted screenshot pixels.
internal sealed class MarkerVision : IVisionPipeline {
    internal VisionResult? Last;
    public Task<VisionResult> AnalyzeAsync(CapturedScreen s,CancellationToken token) {
        var bmp=new BitmapImage();bmp.BeginInit();bmp.CacheOption=BitmapCacheOption.OnLoad;bmp.UriSource=new Uri(s.Frame.ScreenshotPath);bmp.EndInit();
        var b=new FormatConvertedBitmap(bmp,PixelFormats.Bgra32,null,0);int stride=b.PixelWidth*4;
        var bytes=new byte[stride*b.PixelHeight];b.CopyPixels(bytes,stride,0);
        var colors=Markers.All.ToDictionary(m=>m.Color);var bounds=new Dictionary<int,(int x,int y,int right,int bottom,int count)>();
        for(int y=0;y<b.PixelHeight;y++)for(int x=0;x<b.PixelWidth;x++){
            var i=y*stride+x*4;int c=(bytes[i+2]<<16)|(bytes[i+1]<<8)|bytes[i];
            if(!colors.ContainsKey(c))continue;
            if(bounds.TryGetValue(c,out var v))bounds[c]=(Math.Min(v.x,x),Math.Min(v.y,y),Math.Max(v.right,x),Math.Max(v.bottom,y),v.count+1);
            else bounds[c]=(x,y,x,y,1);
        }
        var regions=new List<VisualToken>();var texts=new List<VisualToken>();
        foreach(var (c,v) in bounds){
            if(v.count<30)continue;
            var m=colors[c];var box=new Region((double)v.x/s.Width,(double)v.y/s.Height,(double)(v.right-v.x+1)/s.Width,(double)(v.bottom-v.y+1)/s.Height);
            if(m.Category is not null)regions.Add(new(m.Category,1,box));
            texts.Add(new(m.Name,1,new(box.X+box.Width*.2,box.Y+box.Height*.2,box.Width*.6,box.Height*.6)));
        }
        var hashes=new Dictionary<string,string>();
        foreach(var region in regions){var box=region.Bounds;var sample=new List<byte>();
            for(int yy=(int)((box.Y+box.Height*.15)*s.Height);yy<(box.Y+box.Height*.85)*s.Height;yy++)
                for(int xx=(int)((box.X+box.Width*.15)*s.Width);xx<(box.X+box.Width*.85)*s.Width;xx++){int index=yy*stride+xx*4;sample.Add(bytes[index]);sample.Add(bytes[index+1]);sample.Add(bytes[index+2]);}
            hashes[FieldTrader.Navigation.VisualRegionKey.For(region.Label,box)]=Convert.ToHexString(SHA256.HashData(sample.ToArray()));
        }
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Last=new(s.Frame.Id,s.Frame.CapturedAt,s.ContentHash,s.Width,s.Height,regions.ToArray(),texts.ToArray(),0,0,0,Recognizer:"PixelFixture: screenshot colors; NOT YOLO/OCR",RegionContent:hashes));
    }
}

internal sealed class LabClassifier : FieldTrader.Navigation.IPageClassifier {
    public FieldTrader.Navigation.PageObservation Classify(CapturedScreen s,VisionResult v){
        var pages=new[]{"Home","Detail","Warehouse","Contact"}.Where(name=>v.Texts.Any(t=>t.Label=="Page "+name)).ToArray();
        var page=pages.Length==1?pages[0]:"Unknown";
        var layout=string.Join("|",v.Regions.OrderBy(t=>t.Label).ThenBy(t=>t.Bounds.X).Select(t=>$"{t.Label}:{t.Bounds}"));
        return new(s,v,page,layout,[]);
    }
}
