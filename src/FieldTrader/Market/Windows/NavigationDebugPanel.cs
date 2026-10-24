using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using FieldTrader.Navigation;
using FieldTrader.NavigationLab;
using Microsoft.Win32;
using VisualTarget=FieldTrader.Navigation.VisualTarget;
namespace FieldTrader.Market;
public sealed class NavigationDebugPanel : DockPanel {
    private readonly string root;private readonly Func<string> configuration;private readonly Func<CatalogItem[]> catalog;private readonly Action<bool> busyChanged;
    private readonly ComboBox mode=new(){ItemsSource=new[]{"只读识别","模拟导航（本地测试界面）","本地真实导航（仅测试窗口）"},SelectedIndex=0,MinWidth=190};
    private readonly ComboBox source=new(){ItemsSource=new[]{"历史游戏截图","游戏当前页（仅只读）","WPF本地测试窗口"},SelectedIndex=0,MinWidth=160};
    private readonly ComboBox destination=new(){ItemsSource=new[]{"Market","ContactList","Warehouse"},SelectedIndex=0,MinWidth=100};
    private readonly TextBox contact=new(){Text="迪克·文森",Width=110},product=new(){Width=160,ToolTip="可选：已确认商品名"};
    private readonly CheckBox authorize=new(){Content="授权新建的本地测试窗口接收真实输入",IsChecked=false,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock status=new(){Text="默认只读；真实输入只能进入FieldTrader自己的测试窗口。",TextWrapping=TextWrapping.Wrap};
    private readonly TextBox details=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly Image current=new(){Stretch=Stretch.Uniform},before=new(){Stretch=Stretch.Uniform},after=new(){Stretch=Stretch.Uniform};
    private readonly Button run=new(){Content="开始新鲜识别 / 导航",Margin=new Thickness(4)};
    private NavigationControl? control;private CancellationTokenSource? cancel;private bool latched;private bool collecting;
    private NavigationSnapshot? last;
    public bool Busy=>cancel is not null;
    public NavigationDebugPanel(string directory,Func<string> config,Func<CatalogItem[]> items,Action<bool> onBusy) {
        root=Path.Combine(directory,"visual-navigation");configuration=config;catalog=items;busyChanged=onBusy;
        var textFactory=new FrameworkElementFactory(typeof(TextBlock));textFactory.SetBinding(TextBlock.TextProperty,new Binding());var template=new DataTemplate{VisualTree=textFactory};
        mode.ItemTemplate=template;source.ItemTemplate=template;destination.ItemTemplate=template;
        var header=new StackPanel();SetDock(header,Dock.Top);Children.Add(header);var row=new WrapPanel();header.Children.Add(row);
        foreach(var element in new UIElement[]{source,mode,destination,contact,product,authorize})row.Children.Add(element);
        var commands=new WrapPanel();header.Children.Add(commands);commands.Children.Add(run);run.Click+=async(_,_)=>await Start();
        void Button(string title,Action action){var b=new Button{Content=title,Margin=new Thickness(4)};b.Click+=(_,_)=>action();commands.Children.Add(b);}
        Button("暂停",()=>control?.Pause());Button("取消",()=>cancel?.Cancel());Button("紧急停止",Stop);
        Button("恢复（重新截图）",async()=>{if(!Busy&&!latched)await Start();});Button("新建会话 / 解除锁存",()=>{if(!Busy){latched=false;status.Text="新会话已准备；下次从新截图开始。";}});
        header.Children.Add(status);
        var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(2,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});Children.Add(grid);
        var images=new TabControl();images.Items.Add(new TabItem{Header=ImageHeader("当前截图 / 目标与坐标"),Content=current});images.Items.Add(new TabItem{Header=ImageHeader("操作前"),Content=before});images.Items.Add(new TabItem{Header=ImageHeader("操作后"),Content=after});grid.Children.Add(images);Grid.SetColumn(details,1);grid.Children.Add(details);
    }
    private static object ImageHeader(string text)=>new Border{Background=new SolidColorBrush(Color.FromRgb(35,53,68)),Padding=new Thickness(6),Child=new TextBlock{Text=text,Foreground=Brushes.AliceBlue}};
    public void SetCollecting(bool value){collecting=value;run.IsEnabled=!value&&!Busy;}
    public void Stop(){latched=true;control?.EmergencyStop();cancel?.Cancel();status.Text="急停已锁存；旧坐标作废。";}
    private async Task Start() {
        if(Busy||collecting)return;
        if(latched){status.Text="急停锁存中，请明确新建会话。";return;}
        var selected=(ExecutionMode)mode.SelectedIndex;int origin=source.SelectedIndex;
        if(origin!=2&&selected!=ExecutionMode.RecognizeOnly){status.Text="游戏及历史图仅允许只读；模拟和真实输入请选择本地测试窗口。";return;}
        if(selected==ExecutionMode.Real&&authorize.IsChecked!=true){status.Text="本地真实模式需勾选测试窗口授权；游戏输入没有开放。";return;}
        string? file=null;
        if(origin==0){var dialog=new OpenFileDialog{Filter="截图|*.png;*.jpg;*.jpeg"};if(dialog.ShowDialog()!=true)return;file=dialog.FileName;}
        string config=configuration();if(origin!=2&&!File.Exists(config)){status.Text="先选择有效模型配置";return;}
        var dest=destination.SelectedItem?.ToString()??"Market";var person=contact.Text;var name=product.Text;
        var folder=Path.Combine(root,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));Directory.CreateDirectory(folder);
        cancel=new();control=new();run.IsEnabled=false;busyChanged(true);var ct=cancel.Token;
        try {
            if(origin==2){
                status.Text=$"{selected} · 明确适配的截图像素测试识别器，不是YOLO/OCR";
                var result=await LocalVisualNavigation.Run(selected,folder,control,Publish,ct);
                status.Text=$"{result.Result.State} · 已验证 {result.Result.Completed}/3 步 · 真实点击 {result.NativeClicks} · 模拟点击 {result.SimulatedClicks} · 错击 {result.WrongClicks} · {folder}";
            } else {
                if(origin==1){status.Text="4秒内切到游戏；仅截图识别，不发送输入";await Task.Delay(4000,ct);}
                var items=await Task.Run(catalog,ct);
                await Task.Run(async()=>{
                    IScreenSource capture=origin==0?new ImageFileCapture(file!):new GameScreenCapture(Path.Combine(folder,"screens"));
                    await using var vision=new PythonVisionPipeline(config);var log=new NavigationEvidenceLog(folder,Publish);
                    var screen=await capture.CaptureAsync(ct);var raw=await vision.AnalyzeAsync(screen,ct);if(!screen.Live)screen=screen with{Width=raw.Width,Height=raw.Height};
                    var observation=new MarketPageClassifier().Classify(screen,raw);log.Observation(observation);
                    var item=items.SingleOrDefault(i=>i.Confirmed&&Geometry.Normalize(i.Name)==Geometry.Normalize(name));
                    var task=VisualNavigationRoutes.Build(observation,dest,person,item);
                    var batch=new BatchActionPlanner(new FieldTrader.Navigation.NavigationPlanner(new())).Plan(observation,task,0);log.Plan(batch);
                    log.Append(new(DateTimeOffset.UtcNow,task.Id,NavigationState.SUCCESS,"只读计划：未发送输入，未声称导航完成",screen.Frame.Id));
                },ct);
            }
        }catch(OperationCanceledException){status.Text="已取消；旧坐标作废";}catch(Exception e){status.Text="已停止："+e.Message;File.WriteAllText(Path.Combine(folder,"failure.txt"),e.ToString());}
        finally{control.Dispose();control=null;cancel.Dispose();cancel=null;run.IsEnabled=!collecting;busyChanged(false);}
    }
    internal void PreviewMode(ExecutionMode value){source.SelectedIndex=2;mode.SelectedIndex=(int)value;authorize.IsChecked=value==ExecutionMode.Real;status.Text=$"{value} · 本地截图像素适配识别器，不是YOLO/OCR · 新截图验证完成";}
    internal void Publish(NavigationSnapshot snapshot){Dispatcher.BeginInvoke(()=>Display(snapshot));}
    internal void Display(NavigationSnapshot snapshot) {
        last=snapshot;
        var p=snapshot.Page;
        if(p is null)return;
        var targets=snapshot.Plan?.Source.Screen.Frame.Id==p.Screen.Frame.Id?snapshot.Plan.Actions.Select(a=>a.Target).ToArray():[];
        current.Source=Draw(p,targets);
        if(snapshot.Before is {} pre)before.Source=Draw(pre,[]);if(snapshot.After is {} post)after.Source=Draw(post,[]);
        details.Text=$"页面 {p.Page} · 置信指标 {p.Confidence:P1}\n识别器：{p.Vision.Recognizer}\nFrame {p.Screen.Frame.Id}\n时间 {p.Screen.Frame.CapturedAt:O}\n窗口 {p.Screen.WindowHandle} · 原点 ({p.Screen.ScreenX},{p.Screen.ScreenY}) · {p.Screen.Width}×{p.Screen.Height}\n"+
            string.Join("\n",targets.Select(t=>$"目标 {t.Name} · {t.Category} · 屏幕物理点击 ({t.Click.X},{t.Click.Y})\n来源：{string.Join(";",t.Sources??[])}"))+"\n"+
            (snapshot.Verification is {} v?$"验证 {v.Passed}：{v.Reason}\n":"")+
            (snapshot.Event is {} e?$"任务 {e.TaskId} · 步骤 {e.StepId} · {e.State}\n{e.Reason}\n":"")+
            "分段耗时(ms)：\n"+JsonSerializer.Serialize(new{Model=p.Vision.Performance,Session=snapshot.LatestTimings},NavigationEvidenceLog.Json)+"\n检测区域（来源见识别器）：\n"+string.Join("\n",p.Vision.Regions.Select(t=>$"{t.Label} {t.Confidence:P1} {t.Bounds}"))+"\nOCR：\n"+string.Join("\n",p.Vision.Texts.Select(t=>$"{t.Label} {t.Confidence:P1}"));
    }
    internal static ImageSource Draw(PageObservation p,VisualTarget[] targets) {
        var background=MarketWindow.RenderPreview(p.Screen.Frame.ScreenshotPath,p.Vision);var drawing=new DrawingGroup();using(var dc=drawing.Open()) {
            dc.DrawImage(background,new Rect(0,0,p.Vision.Width,p.Vision.Height));
            foreach(var target in targets){var r=target.Bounds;dc.DrawRectangle(null,new Pen(Brushes.Gold,4),new(r.X*p.Vision.Width,r.Y*p.Vision.Height,r.Width*p.Vision.Width,r.Height*p.Vision.Height));
                double x=target.Click.X-p.Screen.ScreenX,y=target.Click.Y-p.Screen.ScreenY;dc.DrawLine(new Pen(Brushes.Red,3),new(x-12,y),new(x+12,y));dc.DrawLine(new Pen(Brushes.Red,3),new(x,y-12),new(x,y+12));}
        }drawing.Freeze();return new DrawingImage(drawing);
    }
}
