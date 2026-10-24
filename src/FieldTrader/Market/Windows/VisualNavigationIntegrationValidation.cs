using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FieldTrader.Navigation;
using FieldTrader.NavigationLab;
namespace FieldTrader.Market;
public static class VisualNavigationIntegrationValidation {
    public static async Task CancelBinding(string output) {
        Application.Current.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        using var control=new NavigationControl();using var cancel=new CancellationTokenSource();
        var result=await LocalVisualNavigation.Run(ExecutionMode.Simulated,output,control,s=>{if(s.Event?.State==NavigationState.EXECUTE)cancel.Cancel();},cancel.Token);
        bool passed=result.Result.State==NavigationState.PAUSED&&result.NativeClicks==0&&result.SimulatedClicks==0;
        File.WriteAllText(Path.Combine(output,"cancel-binding.json"),JsonSerializer.Serialize(new{Passed=passed,Result=result},NavigationEvidenceLog.Json));
        if(!passed)throw new InvalidOperationException("Cancellation binding did not stop at input boundary");
    }
    public static void RenderReadOnly(string input,string output) {
        Directory.CreateDirectory(output);
        foreach(var path in Directory.GetFiles(input,"*-readonly.json").Order()) {
            using var doc=JsonDocument.Parse(File.ReadAllText(path));var row=doc.RootElement;
            var page=row.GetProperty("Observation").Deserialize<PageObservation>(NavigationEvidenceLog.Json)!;
            var actions=row.GetProperty("Targets").EnumerateArray().Where(t=>t.GetProperty("Plan").ValueKind!=JsonValueKind.Null).Select(t=>t.GetProperty("Plan").Deserialize<PlannedAction>(NavigationEvidenceLog.Json)!).ToArray();
            var image=NavigationDebugPanel.Draw(page,actions.Select(a=>a.Target).ToArray());var visual=new DrawingVisual();using(var dc=visual.RenderOpen()) {
                dc.DrawImage(image,new Rect(0,0,page.Vision.Width,page.Vision.Height));
                dc.DrawRectangle(Brushes.Black,null,new Rect(0,0,page.Vision.Width,70));
                dc.DrawText(new FormattedText($"READ ONLY · {page.Page} · {actions.Length} plans · NO INPUT\n{page.Screen.Frame.Id} | green YOLO / blue OCR / gold fused region / red planned point",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),22,Brushes.White,1),new Point(8,4));
            }
            var rendered=new RenderTargetBitmap(page.Vision.Width,page.Vision.Height,96,96,PixelFormats.Pbgra32);rendered.Render(visual);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(rendered));using var file=File.Create(Path.Combine(output,Path.GetFileNameWithoutExtension(row.GetProperty("Image").GetString())+"-overlay.png"));encoder.Save(file);
        }
    }
    public static async Task Run(string output) {
        Application.Current.ShutdownMode=ShutdownMode.OnExplicitShutdown;Directory.CreateDirectory(output);
        var window=new MarketWindow(Path.Combine(output,"ui-data")){Left=-16000,Top=-16000,ShowInTaskbar=false};window.Show();await window.Initialization;
        var reports=new List<LocalSessionResult>();NavigationSnapshot? last=null;
        try {
            foreach(var mode in new[]{ExecutionMode.RecognizeOnly,ExecutionMode.Simulated,ExecutionMode.Real}) {
                using var control=new NavigationControl();
                var result=await LocalVisualNavigation.Run(mode,Path.Combine(output,mode.ToString()),control,s=>{last=s;window.NavigationDebug.Publish(s);});reports.Add(result);
                if(mode==ExecutionMode.Real&&last is not null) {
                    await Task.Delay(150);window.SelectNavigationPreview();window.NavigationDebug.PreviewMode(mode);
                    window.NavigationDebug.Display(last with{Page=last.Plan?.Source??last.Page});window.UpdateLayout();
                    var rendered=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);rendered.Render(window);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(rendered));using var file=File.Create(Path.Combine(output,"visual-navigation-debug.png"));encoder.Save(file);
                }
            }
            bool passed=reports[0].Result.Completed==0&&reports[0].NativeClicks==0&&reports[1].Result.Completed==3&&reports[1].SimulatedClicks==3&&reports[1].NativeClicks==0&&reports[2].Result.Completed==3&&reports[2].NativeClicks==3&&reports.All(r=>r.WrongClicks==0&&r.Result.State==NavigationState.SUCCESS);
            File.WriteAllText(Path.Combine(output,"integration.json"),JsonSerializer.Serialize(new{Passed=passed,Recognition="Adapted screenshot pixel fixture; NOT YOLO/OCR",Reports=reports},NavigationEvidenceLog.Json));
            if(!passed)throw new InvalidOperationException("Integrated visual-navigation acceptance failed; inspect integration.json");
        }finally{window.Close();}
    }
}
