using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FieldTrader.Market;
using FieldTrader.Navigation;
using Geometry = FieldTrader.Market.Geometry;

namespace FieldTrader.NavigationLab;

internal sealed class LabLog(string directory, LocalScreenSource capture, MarkerVision vision) : INavigationLog, INavigationTiming {
    internal readonly List<NavigationEvent> Events=[];
    internal readonly Dictionary<string,List<double>> Timings=[];
    public void Measure(string stage,double milliseconds){lock(Timings){if(!Timings.TryGetValue(stage,out var list))Timings[stage]=list=[];list.Add(milliseconds);}}
    public void Append(NavigationEvent entry){
        lock(Events){Events.Add(entry);File.AppendAllText(Path.Combine(directory,"navigation.jsonl"),JsonSerializer.Serialize(entry)+"\n");}
        if(entry.State==NavigationState.EXECUTE&&capture.Last is {} s&&vision.Last is {} v){
            File.WriteAllText(Path.Combine(directory,s.Frame.Id+"-vision.json"),JsonSerializer.Serialize(new{Screen=s,Vision=v,State=entry.State.ToString(),entry.StepId},new JsonSerializerOptions{WriteIndented=true}));
            Overlay.Save(s,v,entry.StepId,Path.Combine(directory,s.Frame.Id+"-overlay.png"));
        }
    }
}

internal static class Overlay {
    internal static void Save(CapturedScreen s,VisionResult v,string? name,string output){
        var bmp=new BitmapImage();bmp.BeginInit();bmp.CacheOption=BitmapCacheOption.OnLoad;bmp.UriSource=new Uri(s.Frame.ScreenshotPath);bmp.EndInit();
        var visual=new DrawingVisual();using(var d=visual.RenderOpen()){
            d.DrawImage(bmp,new Rect(0,0,s.Width,s.Height));
            foreach(var t in v.Regions){var r=new Rect(t.Bounds.X*s.Width,t.Bounds.Y*s.Height,t.Bounds.Width*s.Width,t.Bounds.Height*s.Height);d.DrawRectangle(null,new Pen(Brushes.Cyan,2),r);}
            foreach(var t in v.Texts){var r=new Rect(t.Bounds.X*s.Width,t.Bounds.Y*s.Height,t.Bounds.Width*s.Width,t.Bounds.Height*s.Height);d.DrawRectangle(null,new Pen(Brushes.LimeGreen,1),r);}
            var chosen=v.Texts.FirstOrDefault(t=>t.Label==name);
            if(chosen!=null){var box=v.Regions.FirstOrDefault(r=>Geometry.Contains(r.Bounds,chosen.Bounds));if(box!=null){var r=new Rect(box.Bounds.X*s.Width,box.Bounds.Y*s.Height,box.Bounds.Width*s.Width,box.Bounds.Height*s.Height);d.DrawRectangle(null,new Pen(Brushes.Gold,4),r);var x=r.X+r.Width/2;var y=r.Y+r.Height/2;d.DrawLine(new Pen(Brushes.Red,2),new Point(x-10,y),new Point(x+10,y));d.DrawLine(new Pen(Brushes.Red,2),new Point(x,y-10),new Point(x,y+10));}}
            d.DrawRectangle(Brushes.Black,null,new Rect(0,s.Height-60,s.Width,60));
            d.DrawText(new FormattedText($"FIXTURE pixels (not YOLO/OCR): cyan regions / green labels / gold selected / red click\nFrame {s.Frame.Id} | EXECUTE {name} | confidence=1 fixture | physical {s.Width}x{s.Height}",CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),13,Brushes.White,1),new Point(8,s.Height-56));
        }
        var rendered=new RenderTargetBitmap(s.Width,s.Height,96,96,PixelFormats.Pbgra32);rendered.Render(visual);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(rendered));using var file=File.Create(output);png.Save(file);
    }
}

internal sealed class RealInput(LabWindow window) : IInputController {
    private readonly WindowsInputController native=new(window.Hwnd);
    internal Func<int,PlannedAction,Task>? Before,After;
    internal int Calls,InFlight,MaximumInFlight;
    internal readonly List<object> Sent=[];
    public bool IsSimulation=>false;
    public async Task ExecuteAsync(PlannedAction a,CapturedScreen s,CancellationToken ct){
        int index=Interlocked.Increment(ref Calls);var active=Interlocked.Increment(ref InFlight);MaximumInFlight=Math.Max(active,MaximumInFlight);
        try{
            if(Before!=null)await Before(index,a);
            await window.UI(()=>window.ExpectedName=a.Target.Name);
            var timer=Stopwatch.StartNew();
            await native.ExecuteAsync(a,s,ct);var nativeMs=timer.Elapsed.TotalMilliseconds;
            await Task.Delay(65); // Native API completion is NOT UI verification.
            await window.UI(()=>Sent.Add(new{At=DateTimeOffset.UtcNow,a.Step.Id,a.Target.Click,NativeCallMs=nativeMs,ReceivedCount=window.Received.Count,window.Count}));
            if(After!=null)await After(index,a);
        }finally{Interlocked.Decrement(ref InFlight);}
    }
}

internal sealed record CaseResult(string Scenario,int Iteration,bool Passed,string Outcome,string Reason,int PhysicalClicks,int WrongClicks,int WheelEvents,bool ButtonHeld,double TotalMs,Dictionary<string,double[]> Stages,Received[] Received);

internal sealed class Suite(LabWindow w,int repetitions=20,string? onlyScenario=null) {
    internal static readonly string[] Scenarios=["single","two-targets","six-targets","interval","dynamic-relocate","unexpected-layout","page-change","dialog","scroll","scroll-boundary","cancel","pause-resume","emergency-latch","input-exception","move-fresh","resize-fresh","move-stale","resize-stale","focus-loss","minimize","closed-window","exclusive-tasks","queued-cancel"];
    private readonly List<CaseResult> results=[];
    internal async Task Run(){
        if(!OperatingSystem.IsWindows()||!Environment.UserInteractive)throw new InvalidOperationException("Windows interactive desktop required");
        var desktop=Native.OpenInputDesktop(0,false,1);if(desktop==IntPtr.Zero)throw new IOException("Input desktop unavailable");Native.CloseDesktop(desktop);
        File.WriteAllText(Path.Combine(w.Output,"environment.json"),JsonSerializer.Serialize(new{OS=Environment.OSVersion.ToString(),ProcessArchitecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),InputDesktop=true,Dpi=Native.GetDpiForWindow(w.Hwnd),Monitors=Native.GetSystemMetrics(80),VirtualX=Native.GetSystemMetrics(76),VirtualY=Native.GetSystemMetrics(77),VirtualWidth=Native.GetSystemMetrics(78),VirtualHeight=Native.GetSystemMetrics(79),FixtureRecognition=true,RealSendInput=true},new JsonSerializerOptions{WriteIndented=true}));
        foreach(var name in onlyScenario is null?Scenarios:[onlyScenario])for(int i=0;i<repetitions;i++){
            if(w.StopSuite)break;
            await w.UI(()=>{w.WindowState=WindowState.Normal;w.Left=80;w.Top=50;w.Width=1060;w.Height=850;w.Activate();});
            await w.Prepare();await w.SetStatus($"{name} · {i+1}/20 · real SendInput · local only");
            if(name=="minimize")await Task.Delay(650); // Settle OS restore animation before measuring this negative case.
            var r=await RunCase(name,i);results.Add(r);
            File.AppendAllText(Path.Combine(w.Output,"cases.jsonl"),JsonSerializer.Serialize(r)+"\n");
            if(File.Exists(Path.Combine(w.Output,"STOP")))break;
        }
        var summary=results.GroupBy(r=>r.Scenario).Select(g=>new{Scenario=g.Key,Trials=g.Count(),Passed=g.Count(r=>r.Passed),SuccessRate=(double)g.Count(r=>r.Passed)/g.Count(),WrongClicks=g.Sum(r=>r.WrongClicks),AverageMs=g.Average(r=>r.TotalMs),P95Ms=P95(g.Select(r=>r.TotalMs)),Stages=g.SelectMany(r=>r.Stages).GroupBy(k=>k.Key).ToDictionary(k=>k.Key,k=>new{Samples=k.Sum(a=>a.Value.Length),MeanMs=k.SelectMany(a=>a.Value).Average(),P95Ms=P95(k.SelectMany(a=>a.Value))})}).ToArray();
        File.WriteAllText(Path.Combine(w.Output,"summary.json"),JsonSerializer.Serialize(new{ActualGui=true,Recognition="Screenshot pixel fixture; NOT YOLO/OCR",YoloMs=(double?)null,OcrMs=(double?)null,ColdSample=results.FirstOrDefault(),WarmSummaryExcludesCold=summary.Select(s=>new{s.Scenario,s.Trials,s.Passed,s.SuccessRate,s.WrongClicks,WarmMeanMs=results.Where(r=>r.Scenario==s.Scenario&&r.Iteration>0).Select(r=>(double?)r.TotalMs).Average()}),Summary=summary,Untested=new[]{"100/125/150/200% other than actual DPI","negative coordinates/multiple monitors if absent","external application focus switch","audio hardware concurrency","game input"}},new JsonSerializerOptions{WriteIndented=true}));
        await w.SetStatus($"DONE: {results.Count(r=>r.Passed)}/{results.Count} passed | wrong clicks {results.Sum(r=>r.WrongClicks)} | evidence {w.Output}");
    }
    private static double P95(IEnumerable<double> values){var a=values.Order().ToArray();return a[Math.Max(0,(int)Math.Ceiling(a.Length*.95)-1)];}
    internal static TargetQuery Query(string name)=>new(name,name=="Close"?"secondary_button":name=="Scroll"?"material_row":name is "Next" or "Shift" or "Popup"?"market_entry":"product_card",new(0,0,1,1),"LocalLab",new(0,0,1,1));
    internal static NavigationStep Step(string name,int count,string before="Home",string? after=null,bool dynamic=false,InputOperation operation=InputOperation.Click,int delta=0)=>
        new(name,before,Query(name),operation,after??before,TimeSpan.FromSeconds(10),FailurePolicy.Pause,true,dynamic?LayoutEffect.MayChange:LayoutEffect.Stable,RequiredAfterText:operation==InputOperation.Scroll?["Scrolled"]:["Count "+count],ScrollDelta:delta);
    private async Task<CaseResult> RunCase(string scenario,int iteration){
        var directory=Path.Combine(w.Output,scenario,iteration.ToString("D2"));Directory.CreateDirectory(directory);
        var capture=new LocalScreenSource(w.Hwnd,Path.Combine(directory,"screens"));var vision=new MarkerVision();var log=new LabLog(directory,capture,vision);var input=new RealInput(w);
        using var control=new NavigationControl();using var cancel=new CancellationTokenSource();await w.UI(()=>{w.ActiveControl=control;w.ActiveCancel=cancel;});
        var executor=new NavigationExecutor(capture,vision,new LabClassifier(),input,log);
        NavigationStep[] steps=[Step("A",1)];bool expectedStop=false;int expectedClicks=1;string? error=null;Window? decoy=null;LabWindow? disposable=null;
        var total=Stopwatch.StartNew();NavigationResult result=new(NavigationState.FAILED,0,"not run");
        async Task PauseThenResume(){control.Resume();var resumed=await executor.RunAsync(new("resume-"+Guid.NewGuid(),[Step("B",2)],ExecutionMode.Real,ActionInterval:TimeSpan.FromMilliseconds(80)),control);if(resumed.State!=NavigationState.SUCCESS)throw new Exception("Fresh resume failed: "+resumed.Reason);}
        try{
            switch(scenario){
                case "ui-stop-probe":steps=[Step("A",1) with{Timeout=TimeSpan.FromMinutes(2)},Step("B",2) with{Timeout=TimeSpan.FromMinutes(2)}];expectedStop=true;break;
                case "two-targets":steps=[Step("A",1),Step("B",2)];expectedClicks=2;break;
                case "six-targets":steps="ABCDEF".Select((c,i)=>Step(c.ToString(),i+1)).ToArray();expectedClicks=6;break;
                case "interval":steps=[Step("A",1),Step("B",2)];expectedClicks=2;break;
                case "dynamic-relocate":await w.UI(()=>w.MoveAfterA=true);steps=[Step("A",1,dynamic:true),Step("B",2)];expectedClicks=2;break;
                case "unexpected-layout":await w.UI(()=>w.MoveAfterA=true);steps=[Step("A",1),Step("B",2)];expectedStop=true;break;
                case "page-change":steps=[Step("Next",1,after:"Detail",dynamic:true),Step("B",2,before:"Detail")];expectedClicks=2;break;
                case "dialog":steps=[Step("Popup",1,dynamic:true),Step("Close",2,dynamic:true)];expectedClicks=2;break;
                case "scroll":case "scroll-boundary":
                    steps=[Step("Scroll",0,dynamic:true,operation:InputOperation.Scroll,delta:-1200)];expectedClicks=0;break;
                case "cancel":case "pause-resume":case "emergency-latch":
                    steps=[Step("A",1),Step("B",2),Step("C",3)];expectedStop=true;
                    input.After=(i,_)=>{if(i==1){if(scenario=="cancel")cancel.Cancel();else if(scenario=="pause-resume")control.Pause();else control.EmergencyStop();}return Task.CompletedTask;};break;
                case "input-exception":steps=[Step("A",1),Step("B",2)];expectedStop=true;
                    input.Before=(i,_)=>i==2?Task.FromException(new IOException("Injected adapter exception before second SendInput")):Task.CompletedTask;break;
                case "move-fresh":await w.UI(()=>{w.Left=390;w.Top=190;});await Task.Delay(100);break;
                case "resize-fresh":await w.UI(()=>{w.Width=1130;w.Height=900;});await Task.Delay(100);break;
                case "move-stale":case "resize-stale":case "focus-loss":case "minimize":
                    expectedStop=true;expectedClicks=0;
                    input.Before=async(i,_)=>{if(i!=1)return;await w.UI(()=>{
                        if(scenario=="move-stale")w.Left+=160;
                        if(scenario=="resize-stale")w.Width+=70;
                        if(scenario=="minimize")w.WindowState=WindowState.Minimized;
                        if(scenario=="focus-loss"){decoy=new Window{Title="Navigation Lab safety decoy — NO INPUT",Width=420,Height=240,Left=1700,Top=70,Content=new TextBlock{Text="Foreground decoy. No clicks should arrive here.",TextWrapping=TextWrapping.Wrap}};decoy.PreviewMouseDown+=(_,_)=>w.WrongClicks++;decoy.Show();decoy.Activate();}
                    });await Task.Delay(100);};break;
                case "closed-window":
                    expectedStop=true;expectedClicks=0;
                    await w.UI(()=>{disposable=new LabWindow(Path.Combine(directory,"temporary-window"));disposable.Show();disposable.Activate();});await Task.Delay(650);
                    var tempCapture=new LocalScreenSource(disposable!.Hwnd,Path.Combine(directory,"screens"));var tempVision=new MarkerVision();var tempLog=new LabLog(directory,tempCapture,tempVision);var tempInput=new RealInput(disposable){Before=async(_,_)=>{await w.UI(()=>disposable.Close());await Task.Delay(50);}};
                    executor=new(tempCapture,tempVision,new LabClassifier(),tempInput,tempLog);log=tempLog;input=tempInput;break;
                case "exclusive-tasks":steps=[Step("A",1),Step("C",2)];expectedClicks=4;break;
                case "queued-cancel":steps=[Step("A",1),Step("C",2)];expectedClicks=2;break;
            }
            var task=new NavigationTask(Guid.NewGuid().ToString("N"),steps,ExecutionMode.Real,BatchMode.StablePage,MaxBatchTime:TimeSpan.FromSeconds(scenario=="ui-stop-probe"?120:10),ActionInterval:TimeSpan.FromMilliseconds(scenario=="ui-stop-probe"?60000:scenario=="interval"?220:80));
            if(scenario is "exclusive-tasks" or "queued-cancel"){
                var first=executor.RunAsync(task,control,cancel.Token);using var other=new NavigationControl();using var queued=new CancellationTokenSource();
                var second=executor.RunAsync(new("second-"+Guid.NewGuid(),[Step("B",3),Step("D",4)],ExecutionMode.Real,ActionInterval:TimeSpan.FromMilliseconds(80)),other,queued.Token);
                if(scenario=="queued-cancel")queued.Cancel();
                result=await first;var r2=await second;
                if(scenario=="exclusive-tasks"&&r2.State!=NavigationState.SUCCESS)throw new Exception("Second task: "+r2.Reason);
                if(scenario=="queued-cancel"&&r2.State!=NavigationState.PAUSED)throw new Exception("Queued cancellation did not pause");
            }else result=await executor.RunAsync(task,control,cancel.Token);
            if(scenario=="pause-resume"){
                if(result.State!=NavigationState.PAUSED)throw new Exception("Pause failed");input.After=null;await PauseThenResume();expectedClicks=2;
            }
            if(scenario=="emergency-latch"){
                var before=w.Count;var next=await executor.RunAsync(task with{Id="latched"},control);
                if(next.State!=NavigationState.PAUSED||w.Count!=before)throw new Exception("Emergency latch failed");
            }
            if(scenario is "scroll" or "scroll-boundary"){
                // Fresh independent tasks after actual scroll; no cached list coordinates.
                input.Before=null;
                for(int n=0;n<24;n++){
                    if(scenario=="scroll"&&vision.Last?.Texts.Any(t=>t.Label=="ListTarget")==true)break;
                    var scrolling=await executor.RunAsync(new(Guid.NewGuid().ToString(),[Step("Scroll",0,dynamic:true,operation:InputOperation.Scroll,delta:-1200)],ExecutionMode.Real),control);
                    if(scrolling.State==NavigationState.PAUSED&&scrolling.Reason.Contains("No observed result change"))break;
                }
                if(scenario=="scroll"){
                    // WPF may BringIntoView a focused list item; treat this click as a dynamic boundary.
                    result=await executor.RunAsync(new(Guid.NewGuid().ToString(),[Step("ListTarget",1,dynamic:true)],ExecutionMode.Real),control);expectedClicks=1;
                }else{
                    var before=await w.UI(()=>w.List.VerticalOffset);
                    result=await executor.RunAsync(new(Guid.NewGuid().ToString(),[Step("Scroll",0,dynamic:true,operation:InputOperation.Scroll,delta:-1200)],ExecutionMode.Real),control);
                    var after=await w.UI(()=>w.List.VerticalOffset);
                    if(before<=0||w.WheelEvents<1||Math.Abs(after-before)>.01)throw new Exception("Scroll boundary not reached by actual wheel input");
                    if(!result.Reason.Contains("No observed result change"))throw new Exception("Boundary must stop for missing positive visual change, not an input failure: "+result.Reason);
                    expectedStop=true; // No new positive change: conservative pause, never claim success.
                }
            }
            await Task.Delay(100);
            if(scenario=="interval"&&w.Received.Count>=2&&(w.Received[1].At-w.Received[0].At).TotalMilliseconds<220)throw new Exception("Configured interval not respected");
            if(scenario=="dynamic-relocate"&&w.Received.Count==2&&Math.Abs(w.Received[1].ScreenX-w.Received[0].ScreenX)<300)throw new Exception("Dynamic second target used old position");
            if(input.MaximumInFlight>1)throw new Exception("Concurrent native input adapters");
            if(scenario=="closed-window"&&(input.Calls!=1||!result.Reason.Contains("Local test window")))throw new Exception("Closed HWND guard was not actually exercised: "+result.Reason);
            if(scenario is "move-stale" or "resize-stale"){
                if(input.Calls!=1||!result.Reason.Contains("Client geometry changed"))throw new Exception("Expected native stale-geometry guard: "+result.Reason);
            }
            if(scenario is "focus-loss" or "minimize"){
                if(input.Calls!=1||!result.Reason.Contains("Local test window"))throw new Exception("Expected native foreground/visibility guard: "+result.Reason);
            }
            if(scenario=="ui-stop-probe"&&(!w.StopSuite||!result.Reason.Contains("Cancelled/emergency")||!File.Exists(Path.Combine(w.Output,"ui-control.jsonl"))))throw new Exception("Probe must be interrupted by the actual UI stop control while running: "+result.Reason);
        }catch(Exception e){error=e.ToString();}
        finally{
            if(decoy!=null)await w.UI(()=>decoy.Close());
            if(disposable!=null)await w.UI(()=>{if(disposable.IsVisible)disposable.Close();});
            await w.UI(()=>{w.ActiveControl=null;w.ActiveCancel=null;});
        }
        total.Stop();
        var held=(Native.GetAsyncKeyState(1)&0x8000)!=0;
        var actual=w.Received.Count(r=>r.Kind=="click");
        bool passed=error==null&&!held&&w.WrongClicks==0&&w.BackgroundClicks==0&&actual==expectedClicks
            &&(expectedStop?result.State==NavigationState.PAUSED:result.State==NavigationState.SUCCESS);
        var item=new CaseResult(scenario,iteration,passed,result.State.ToString(),error??result.Reason,actual,w.WrongClicks+w.BackgroundClicks,w.WheelEvents,held,total.Elapsed.TotalMilliseconds,log.Timings.ToDictionary(k=>k.Key,k=>k.Value.ToArray()),w.Received.ToArray());
        File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(item,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(directory,"native-calls.json"),JsonSerializer.Serialize(input.Sent,new JsonSerializerOptions{WriteIndented=true}));
        return item;
    }
}
