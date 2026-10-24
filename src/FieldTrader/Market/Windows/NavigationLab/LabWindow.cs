using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FieldTrader.Market;
using FieldTrader.Navigation;

namespace FieldTrader.NavigationLab;

public static class NavigationLabHost {
    public static void RunStandalone(string[] args) {
        if(args.Length==3&&args[0]=="--benchmark-evidence"){EvidenceBenchmark.Run(args[1],args[2]);return;}
        var directory=Path.GetFullPath(args.FirstOrDefault()??"artifacts/navigation-lab");Directory.CreateDirectory(directory);
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>File.WriteAllText(Path.Combine(directory,"crash.txt"),e.ExceptionObject.ToString());
        var probe=args.Contains("--ui-stop-probe");
        var onlyScenario=probe?"ui-stop-probe":args.Length==3&&args[1]=="--scenario"?args[2]:null;
        if(onlyScenario!=null&&!probe&&!Suite.Scenarios.Contains(onlyScenario))throw new ArgumentException("Unknown scenario");
        var app=new Application();app.Run(new LabWindow(directory,args.Contains("--smoke")||probe?1:20,onlyScenario));
    }
    public static async Task RunIntegratedSuite(string output,int repetitions=3) {
        Application.Current.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        Directory.CreateDirectory(output);var window=new LabWindow(output,repetitions);window.Show();window.BringLocalForeground();
        try{await Task.Delay(500);await Task.Run(()=>new Suite(window,repetitions).Run());}
        finally{window.Close();}
    }

}

internal sealed record Received(string Target, DateTimeOffset At, double ScreenX, double ScreenY, string Kind);
internal sealed class LabWindow : Window {
    internal readonly string Output;
    internal IntPtr Hwnd {get;private set;}
    internal readonly Canvas Surface=new(){Background=new SolidColorBrush(Color.FromRgb(21,29,40))};
    private readonly TextBlock status=new(){Text="Ready — local window only",TextWrapping=TextWrapping.Wrap,FontSize=15,Height=48};
    private readonly Border countMarker=new(){Width=110,Height=27};
    private readonly Border pageMarker=new(){Width=130,Height=27};
    private readonly Border scrolledMarker=new(){Width=110,Height=27,Visibility=Visibility.Hidden};
    private readonly Dictionary<string,Button> targets=[];
    internal readonly List<Received> Received=[];
    internal int Count,WrongClicks,WheelEvents,BackgroundClicks;
    internal string? ExpectedName;
    internal bool MoveAfterA;
    internal bool RouteMode;
    private bool simulating;
    internal Task InvokeSimulated(string name)=>UI(()=>{simulating=true;try{targets[name].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}finally{simulating=false;}});
    internal ScrollViewer List=null!;
    private Border? dialog;
    private Button start=null!;
    internal NavigationControl? ActiveControl;
    internal CancellationTokenSource? ActiveCancel;
    private Window? preview;
    internal bool Running;
    internal volatile bool StopSuite;
    internal LabWindow(string output,int repetitions=20,string? onlyScenario=null) {
        Output=output;Title="FieldTrader Navigation Lab — LOCAL ONLY";Width=1060;Height=850;Left=80;Top=50;
        SourceInitialized+=(_,_)=>{Hwnd=new WindowInteropHelper(this).Handle;LocalNavigationWindows.Register(Hwnd);};
        Closed+=(_,_)=>LocalNavigationWindows.Remove(Hwnd);
        Background=new SolidColorBrush(Color.FromRgb(235,241,247));
        var root=new DockPanel{Margin=new Thickness(14)};Content=root;
        var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        header.Children.Add(new TextBlock{Text="FieldTrader · Windows Navigation Lab",FontSize=24,FontWeight=FontWeights.Bold});
        header.Children.Add(new TextBlock{Text="REAL SendInput → this window only | Recognition: screenshot pixel FIXTURE, not YOLO/OCR",Margin=new Thickness(0,5,0,8)});
        var controls=new WrapPanel();header.Children.Add(controls);
        void Add(string text,Action action){var b=new Button{Content=text,Padding=new Thickness(12,6,12,6),Margin=new Thickness(3)};b.Click+=(_,_)=>action();controls.Children.Add(b);}
        start=new Button{Content=$"Run real GUI suite ×{repetitions}",Padding=new Thickness(12,6,12,6),Margin=new Thickness(3)};
        controls.Children.Add(start);start.Click+=async(_,_)=>{if(Running)return;Running=true;StopSuite=false;start.IsEnabled=false;preview?.Close();try{await Task.Run(()=>new Suite(this,repetitions,onlyScenario).Run());}catch(Exception e){File.WriteAllText(Path.Combine(Output,"suite-error.txt"),e.ToString());status.Text=e.Message;}finally{Running=false;start.IsEnabled=true;}};
        void StopRecord(string kind)=>File.AppendAllText(Path.Combine(Output,"ui-control.jsonl"),JsonSerializer.Serialize(new{At=DateTimeOffset.UtcNow,Action=kind,Count,Running})+"\n");
        Add("Pause",()=>{StopSuite=true;ActiveControl?.Pause();StopRecord("Pause");});Add("Cancel",()=>{StopSuite=true;ActiveCancel?.Cancel();StopRecord("Cancel");});Add("EMERGENCY STOP",()=>{StopSuite=true;ActiveControl?.EmergencyStop();ActiveCancel?.Cancel();StopRecord("EmergencyStop");});
        Add("Resume fresh",()=>{ActiveControl?.Resume();status.Text="Resume requires a NEW task with fresh capture; run suite or reset.";});
        Add("Reset",()=>{if(!Running)Reset();});Add("Evidence preview",()=>{if(!Running)ShowPreview();});
        header.Children.Add(status);root.Children.Add(Surface);
        Surface.MouseDown+=(_,e)=>{if(e.OriginalSource==Surface){BackgroundClicks++;Record("BACKGROUND","down");}};
        Put(new Border{Width=120,Height=27,Background=Markers.Brush("LocalLab"),Child=Label("LocalLab")},15,10);
        Put(pageMarker,145,10);Put(countMarker,285,10);Put(scrolledMarker,405,10);
        foreach(var (name,x,y) in new[]{("A",30d,80d),("B",210d,80d),("C",390d,80d),("D",30d,165d),("E",210d,165d),("F",390d,165d),("Shift",610d,80d),("Next",610d,165d),("Popup",790d,165d)}){
            var b=MakeTarget(name);targets[name]=b;Put(b,x,y);
        }
        var items=new StackPanel();
        for(int i=0;i<12;i++)items.Children.Add(new TextBlock{Text="Scroll row "+i,Height=35,Background=Brushes.White,Foreground=Brushes.Black});
        var listTarget=MakeTarget("ListTarget");items.Children.Add(listTarget);
        List=new ScrollViewer{Width=420,Height=180,Content=items,Background=Markers.Brush("Scroll"),VerticalScrollBarVisibility=ScrollBarVisibility.Visible,Padding=new Thickness(12)};
        List.PreviewMouseWheel+=(_,_)=>{WheelEvents++;};
        List.ScrollChanged+=(_,_)=>{if(List.VerticalOffset>0){scrolledMarker.Visibility=Visibility.Visible;scrolledMarker.Background=Markers.Brush("Scrolled");scrolledMarker.Child=Label("Scrolled");}};
        Put(List,30,315);
        Put(new TextBlock{Text="A…F: stable targets · Shift: moves B · Next: changes page · Popup: in-client dialog\nScroll list: reach ListTarget. Counter and page markers are read from fresh screenshots.",Foreground=Brushes.White,FontSize=14},480,340);
        Loaded+=(_,_)=>Reset();
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){StopSuite=true;ActiveControl?.EmergencyStop();ActiveCancel?.Cancel();e.Handled=true;}};
        Closing+=(_,e)=>{if(Running){ActiveControl?.EmergencyStop();ActiveCancel?.Cancel();e.Cancel=true;status.Text="Stopping at input boundary; close again after suite stops.";}};
    }
    private static TextBlock Label(string text)=>new(){Text=text,Foreground=Brushes.White,FontSize=16,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
    private void Put(UIElement element,double x,double y){Canvas.SetLeft(element,x);Canvas.SetTop(element,y);Surface.Children.Add(element);}
    private Button MakeTarget(string name){
        var template=new ControlTemplate(typeof(Button));var border=new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty,new System.Windows.Data.Binding("Background"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(presenter);template.VisualTree=border;
        var b=new Button{Content=name,Width=145,Height=55,Background=Markers.Brush(name),Foreground=Brushes.White,FontSize=19,Template=template};
        b.Click+=(_,_)=>{
            if(ExpectedName!=null&&ExpectedName!=name)WrongClicks++;
            Record(name,simulating?"simulated-click":"click");Count++;UpdateCount();
            if(name=="Shift"||name=="A"&&MoveAfterA){Canvas.SetLeft(targets["B"],610);Canvas.SetTop(targets["B"],245);}
            if(name=="Next"){pageMarker.Background=Markers.Brush("Page Detail");pageMarker.Child=Label("Page Detail");}
            if(RouteMode&&name=="C"){pageMarker.Background=Markers.Brush("Page Warehouse");pageMarker.Child=Label("Page Warehouse");}
            if(RouteMode&&name=="D"){pageMarker.Background=Markers.Brush("Page Contact");pageMarker.Child=Label("Page Contact");}
            if(name=="Popup")OpenDialog();if(name=="Close")CloseDialog();
            status.Text=$"Count={Count} | Last={name} | {DateTimeOffset.Now:HH:mm:ss.fff} | received WPF Click (not SendInput return)";
        };return b;
    }
    private void Record(string name,string kind){var p=PointToScreen(Mouse.GetPosition(this));Received.Add(new(name,DateTimeOffset.UtcNow,p.X,p.Y,kind));}
    private void UpdateCount(){countMarker.Background=Markers.Brush("Count "+Math.Min(Count,64));countMarker.Child=Label("Count "+Count);}
    private void OpenDialog(){
        CloseDialog();var panel=new StackPanel{VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};
        panel.Children.Add(new Border{Background=Markers.Brush("Dialog Open"),Width=180,Height=35,Child=Label("Dialog Open")});panel.Children.Add(MakeTarget("Close"));
        dialog=new Border{Width=960,Height=470,Background=new SolidColorBrush(Color.FromRgb(42,46,60)),Child=panel};Put(dialog,5,50);
    }
    private void CloseDialog(){if(dialog!=null)Surface.Children.Remove(dialog);dialog=null;}
    internal void Reset(){
        CloseDialog();MoveAfterA=false;RouteMode=false;Count=WrongClicks=WheelEvents=BackgroundClicks=0;ExpectedName=null;Received.Clear();
        Canvas.SetLeft(targets["B"],210);Canvas.SetTop(targets["B"],80);List.ScrollToTop();scrolledMarker.Visibility=Visibility.Hidden;
        pageMarker.Background=Markers.Brush("Page Home");pageMarker.Child=Label("Page Home");UpdateCount();
    }
    internal void BringLocalForeground(){
        if(Hwnd==IntPtr.Zero)throw new InvalidOperationException("Local HWND not created");
        Topmost=true;Activate();Native.SetForegroundWindow(Hwnd);Focus();Topmost=false;
    }
    internal Task UI(Action action)=>Dispatcher.InvokeAsync(action).Task;
    internal Task<T> UI<T>(Func<T> action)=>Dispatcher.InvokeAsync(action).Task;
    internal async Task Prepare(){await UI(()=>{Reset();status.Text="Running local GUI tests — do not cover this window";});await Task.Delay(110);}
    internal Task SetStatus(string text)=>UI(()=>status.Text=text);
    private void ShowPreview(){
        var files=Directory.GetFiles(Output,"*-overlay.png",SearchOption.AllDirectories);if(files.Length==0){status.Text="Run the suite first to create evidence overlays.";return;}
        var image=new Image{Source=new System.Windows.Media.Imaging.BitmapImage(new Uri(files.Last())),Stretch=Stretch.Uniform};
        preview=new Window{Title="Navigation evidence — cyan fixture / gold selected / red click",Width=1000,Height=800,Content=image};preview.Show();
    }
}
