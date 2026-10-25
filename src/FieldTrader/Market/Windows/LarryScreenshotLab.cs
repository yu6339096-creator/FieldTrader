using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FieldTrader.Navigation;
using FieldTrader.NavigationLab;
namespace FieldTrader.Market;

// An interactive screenshot replay, NOT a copy of the game UI. The recognizer is the
// real unchanged model. Only fixture transitions and independent hit-test truth are authored.
public sealed class LarryScreenshotLab : Window {
    private readonly string images;private readonly Image picture=new(){Stretch=Stretch.Fill};private readonly Canvas overlay=new(){IsHitTestVisible=false};
    private int index=1;private IntPtr hwnd;public int Clicks,WrongClicks,SimulatedClicks,Wheels,SimulatedWheels;
    private readonly List<object> events=[];
    public LarryScreenshotLab(string directory) {
        images=directory;Title="FieldTrader · 拉里本地截图回放（只读交易）";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        Width=Math.Min(SystemParameters.PrimaryScreenWidth-40,1920);Height=Math.Min(SystemParameters.PrimaryScreenHeight-80,1080);Left=10;Top=10;var grid=new Grid();grid.Children.Add(picture);grid.Children.Add(overlay);Content=grid;
        picture.MouseLeftButtonUp+=(_,e)=>{var pt=e.GetPosition(picture);Hit(pt.X/picture.ActualWidth,pt.Y/picture.ActualHeight,false);};
        picture.MouseWheel+=(_,e)=>{var pt=e.GetPosition(picture);if(index==11&&e.Delta<0&&Geometry.Contains(LarryRoutes.Goods,new(pt.X/picture.ActualWidth,pt.Y/picture.ActualHeight,.001,.001))){Wheels++;ShowScrolled();}else events.Add(new{ScrollRejected=true,Fixture=index});e.Handled=true;};
        SourceInitialized+=(_,_)=>{hwnd=new WindowInteropHelper(this).Handle;LocalNavigationWindows.Register(hwnd);};Closed+=(_,_)=>LocalNavigationWindows.Remove(hwnd);ShowFrame(1);
    }
    private BitmapImage Load(int frame){var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.UriSource=new Uri(Path.Combine(images,$"S{frame:D2}.png"));bitmap.EndInit();return bitmap;}
    private void ShowFrame(int frame){overlay.Children.Clear();index=frame;picture.Source=Load(frame);}
    private void ShowScrolled(){
        ShowFrame(11);index=111;double w=picture.ActualWidth,h=picture.ActualHeight;
        var background=new Border{Background=Brushes.Black,Width=.445*w,Height=.75*h};Canvas.SetLeft(background,.045*w);Canvas.SetTop(background,.16*h);overlay.Children.Add(background);
        foreach(var (image,x) in new[]{(12,.05),(14,.158)}){var card=new Image{Source=Load(image),Stretch=Stretch.Fill,Width=.105*w,Height=.24*h};Canvas.SetLeft(card,x*w);Canvas.SetTop(card,.17*h);overlay.Children.Add(card);}
        events.Add(new{Fixture=111,Description="Authored local scroll viewport using actual supplied card crops; fresh pixels re-analyzed by original YOLO/OCR; not a game scroll"});
    }
    private (Region box,int next,string role)[] Allowed()=>index switch {
        1=>[(new(.5,.95,.1,.05),2,"contacts")],
        2=>[(new(.2,.29,.34,.075),3,"Larry-name-row")],
        3=>[(new(.56,.68,.12,.045),4,"shop-entry")],
        4=>[(new(.369,.164,.105,.24),6,"100D-detail"),(new(.047,.41,.105,.24),8,"STO-detail"),(new(.155,.41,.105,.24),10,"TMK-detail"),(new(.099,.12,.052,.035),11,"damage-category"),(new(.254,.12,.05,.035),16,"grenade-category")],
        6=>[(new(.516,.945,.218,.045),4,"cancel")],
        8=>[(new(.516,.945,.218,.045),4,"cancel")],
        10=>[(new(.512,.868,.158,.04),4,"cancel")],
        11=>[(new(.37,.66,.105,.235),13,"surgery-detail"),(new(.047,.12,.052,.035),4,"medical-category"),(new(.254,.12,.05,.035),16,"grenade-category")],
        13=>[(new(.516,.945,.218,.045),11,"cancel")],
        15=>[(new(.516,.945,.218,.045),11,"cancel")],
        111=>[(new(.05,.17,.105,.24),13,"surgery-detail"),(new(.158,.17,.105,.24),15,"painkiller-detail")],
        16=>[(new(.047,.12,.052,.035),4,"medical-category"),(new(.099,.12,.052,.035),11,"damage-category")],
        _=>[]
    };
    private void Hit(double x,double y,bool simulated) {
        int previous=index;var hit=Allowed().Where(a=>x>=a.box.X&&x<=a.box.X+a.box.Width&&y>=a.box.Y&&y<=a.box.Y+a.box.Height).ToArray();
        if(simulated)SimulatedClicks++;else Clicks++;
        if(hit.Length==1)ShowFrame(hit[0].next);else WrongClicks++;
        events.Add(new{Before=previous,After=index,X=x,Y=y,Simulated=simulated,Accepted=hit.Length==1,Role=hit.Length==1?hit[0].role:"WRONG"});
    }
    private sealed class SimInput(LarryScreenshotLab window):IInputController {
        public bool IsSimulation=>true;
        public async Task ExecuteAsync(PlannedAction action,CapturedScreen screen,CancellationToken token) {
            token.ThrowIfCancellationRequested();NavigationSafety.Check(action.Step,action.Target);
            if(action.Step.Operation==InputOperation.Scroll){await window.Dispatcher.InvokeAsync(()=>{if(window.index!=11)throw new NavigationPausedException("Fixture scroll boundary");window.SimulatedWheels++;window.ShowScrolled();});return;}
            if(action.Step.Operation!=InputOperation.Click)throw new NavigationPausedException("Unsupported screenshot replay action");
            await window.Dispatcher.InvokeAsync(()=>window.Hit((action.Target.Click.X-screen.ScreenX)/(double)screen.Width,(action.Target.Click.Y-screen.ScreenY)/(double)screen.Height,true));
        }
    }
    public static async Task Run(string config,string images,string output,ExecutionMode mode=ExecutionMode.Real,Action<NavigationSnapshot>? publish=null,CancellationToken token=default,NavigationControl? sessionControl=null,bool categoriesOnly=false) {
        var previousShutdownMode=Application.Current.ShutdownMode;Application.Current.ShutdownMode=ShutdownMode.OnExplicitShutdown;Directory.CreateDirectory(output);var window=new LarryScreenshotLab(images);window.Show();window.Topmost=true;window.Activate();Native.SetForegroundWindow(window.hwnd);window.Focus();window.Topmost=false;
        try {
            if(categoriesOnly)window.ShowFrame(11);
            await Task.Delay(400,token);using var owned=sessionControl is null?new NavigationControl():null;var control=sessionControl??owned!;
            var log=new NavigationEvidenceLog(output,publish);var capture=new LocalScreenSource(window.hwnd,Path.Combine(output,"screens"));await using var vision=new PythonVisionPipeline(config);
            IInputController input=mode==ExecutionMode.Real?new WindowsInputController(window.hwnd):new SimInput(window);
            LarryDetail[] collected=[];string? failure=null;var itemLocations=new List<object>();bool? itemRouteReady=null;
            try {
                if(categoriesOnly) {
                    // Warm up before planning so cold model initialization never weakens frame expiry.
                    var sample=await new ImageFileCapture(Path.Combine(images,"S11.png")).CaptureAsync(token);await vision.AnalyzeAsync(sample,token);
                    window.Topmost=true;window.Activate();Native.SetForegroundWindow(window.hwnd);window.Focus();window.Topmost=false;
                    foreach(var item in new[]{LarryTargets.Items[0],LarryTargets.Items[6]}) {
                        var step=LarryRoutes.Step("larry:category:"+item.Category,"ContactShop",LarryRoutes.Query(item.Category,"larry_category",new(.04,.115,.32,.045)),"ContactShop") with{RequiredActiveCategory=item.Category};
                        var result=await Task.Run(()=>new NavigationExecutor(capture,vision,new LarryPageClassifier(),input,log).RunAsync(new("category-test-"+item.Id,[step],mode,MaximumFrameAge:TimeSpan.FromSeconds(8),UseCurrentFrameForInput:true),control,token),token);
                        if(result.State!=NavigationState.SUCCESS)throw new NavigationPausedException(result.Reason);
                    }
                    var fresh=await Task.Run(()=>capture.CaptureAsync(token),token);var raw=await vision.AnalyzeAsync(fresh,token);log.Raw(fresh,raw);var page=new LarryPageClassifier().Classify(fresh,raw);log.Observation(page);
                    var resolver=new VisualTargetResolver();itemRouteReady=true;
                    foreach(var target in LarryTargets.Items.Skip(5)) {
                        var step=LarryRoutes.Next(page,target,LarryPageClassifier.ActiveCategory(raw));
                        var found=step is null?new LocationResult(LocationStatus.UNKNOWN,[],"Page not confirmed"):resolver.ResolveDetailed(page,step.Target);PlannedAction? plan=null;
                        if(found.Status==LocationStatus.RESOLVED&&step is not null){plan=new FieldTrader.Navigation.NavigationPlanner(resolver).Locate(page,step,0);log.Plan(new(page,[plan],TimeSpan.FromSeconds(40),TimeSpan.Zero,BatchMode.DynamicPage));}else itemRouteReady=false;
                        itemLocations.Add(new{Item=target.Name,Location=found,Plan=plan,InputExecuted=false,Recipe="RecipeUnknown; no detail evidence"});
                    }
                } else collected=await Task.Run(()=>new LarryCollector(capture,vision,input,log,new MarketDatabase(Path.Combine(output,"larry.sqlite"))).Run("local-"+Guid.NewGuid().ToString("N"),LarryTargets.Items.Take(5).ToArray(),mode,control,token),token);
            }
            catch(Exception e){failure=e.ToString();File.WriteAllText(Path.Combine(output,"failure.txt"),failure);}
            bool passed=failure is null&&window.WrongClicks==0&&(mode==ExecutionMode.RecognizeOnly?window.Clicks==0&&window.SimulatedClicks==0:categoriesOnly?window.index==16&&log.Events.Count(e=>e.State==NavigationState.SUCCESS)==2:collected.Length==5&&log.Events.Count(e=>e.State==NavigationState.SUCCESS)==15);
            var report=new {Passed=passed,Mode=mode,CategoriesOnly=categoriesOnly,Recognition="Actual configured YOLO11m + PP-OCRv4 on fresh local window captures; authored screenshot transitions and scroll viewport, NOT a real game",ItemRouteReady=itemRouteReady,ItemLocations=itemLocations,window.Clicks,window.SimulatedClicks,window.Wheels,window.SimulatedWheels,window.WrongClicks,LastFixture=window.index,Collected=collected,Failure=failure,Transitions=window.events,Events=log.Events,Timings=log.Timings};
            File.WriteAllText(Path.Combine(output,"local.json"),JsonSerializer.Serialize(report,NavigationEvidenceLog.Json));
            if(!passed)throw new NavigationPausedException("Local acceptance incomplete; inspect local.json: "+failure);
        }finally{window.Close();Application.Current.ShutdownMode=previousShutdownMode;}
    }
}
