using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FieldTrader.Market;
public sealed class MarketWindow : Window {
    private readonly string root;
    private readonly MarketDatabase database;
    private readonly TradingPanel trading;
    private readonly ProfitRecommendationPanel profits;
    private readonly NavigationDebugPanel navigation;
    private readonly AutoBuyPanel autoBuy;
    private readonly TextBlock status=new(){Text="行情采集 / 兑换分析 / 隔离模拟",TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock coverage=new();
    private readonly TextBlock seedStatus=new(){Text="正在准备首次运行参考目录…",TextWrapping=TextWrapping.Wrap};
    private readonly TextBox config=new(){MinWidth=470,IsReadOnly=true};
    private readonly TextBox detail=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly DataGrid catalog=new(){IsReadOnly=true,AutoGenerateColumns=false,SelectionMode=DataGridSelectionMode.Single};
    private readonly Image preview=new(){Stretch=Stretch.Uniform};
    private readonly List<Button> workButtons=[];
    private CancellationTokenSource? cancel;
    private MarketWorkflow? workflow;
    private string? lastImage;
    private bool closed;
    internal Task Initialization { get; private set; }=Task.CompletedTask;
    public MarketWindow(string dataRoot) {
        root=Path.Combine(dataRoot,"market");Directory.CreateDirectory(root);database=new(Path.Combine(root,"market.sqlite"));
        trading=new(database,root);profits=new(database);
        navigation=new(root,()=>config.Text,database.Catalog,busy=>{foreach(var b in workButtons)b.IsEnabled=!busy;trading.SetCollecting(busy);});
        autoBuy=new(Path.Combine(root,"real-execution"),()=>config.Text,()=>cancel is not null||trading.Busy||navigation.Busy,busy=>{foreach(var b in workButtons)b.IsEnabled=!busy;trading.SetCollecting(busy);navigation.SetCollecting(busy);});
        Title="市场巡查 · 采集与回放";Width=1100;Height=800;MinWidth=850;MinHeight=600;
        Background=new SolidColorBrush(Color.FromRgb(15,26,35));Foreground=Brushes.AliceBlue;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new DockPanel{Margin=new Thickness(16)};Content=panel;
        var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);
        header.Children.Add(new TextBlock{Text="市场巡查与历史行情",FontSize=22,Margin=new Thickness(0,0,0,10)});
        header.Children.Add(new TextBlock{Text="OCR为主、YOLO辅助。采集与回放不操作游戏；真实提交请使用‘单笔自动买入/兑换’，填写预算并主动开启。",TextWrapping=TextWrapping.Wrap});
        var configRow=new DockPanel{Margin=new Thickness(0,8,0,8)};
        var choose=new Button{Content="选择模型配置"};choose.Click+=(_,_)=>{
            var dialog=new OpenFileDialog{Filter="模型配置|*.json"};if(dialog.ShowDialog()==true){config.Text=dialog.FileName;File.WriteAllText(Path.Combine(root,"config-path.txt"),dialog.FileName);}
        };DockPanel.SetDock(choose,Dock.Right);configRow.Children.Add(choose);configRow.Children.Add(config);header.Children.Add(configRow);
        var saved=Path.Combine(root,"config-path.txt");if(File.Exists(saved)&&File.Exists(File.ReadAllText(saved)))config.Text=File.ReadAllText(saved);
        else if(File.Exists(Path.Combine(AppContext.BaseDirectory,"market-vision.local.json")))config.Text=Path.Combine(AppContext.BaseDirectory,"market-vision.local.json");
        var actions=new WrapPanel();header.Children.Add(actions);
        Add(actions,"导入截图回放",async()=>{
            var dialog=new OpenFileDialog{Filter="截图|*.png;*.jpg;*.jpeg",Multiselect=true};if(dialog.ShowDialog()!=true)return;
            await Run(async(w,t)=>{foreach(var path in dialog.FileNames){t.ThrowIfCancellationRequested();var result=await w.CollectAsync(new ImageFileCapture(path),t);await Dispatcher.InvokeAsync(()=>Show(result,path));if(w.Paused)break;}});
        });
        Add(actions,"采集游戏当前页",async()=>await Run(async(w,t)=>{await Task.Delay(4000,t);var result=await w.CollectAsync(new GameScreenCapture(Path.Combine(root,"screens")),t);await Dispatcher.InvokeAsync(()=>Show(result,null));}));
        Add(actions,"连续只读采集",async()=>await Run(async(w,t)=>{await Task.Delay(4000,t);await w.RunCollectionAsync(new GameScreenCapture(Path.Combine(root,"screens")),r=>Dispatcher.BeginInvoke(()=>Show(r,null)),t);}));
        Add(actions,"预演下一巡查",async()=>{
            if(lastImage is null){status.Text="先导入一张市场截图";return;}
            var target=await Task.Run(()=>new MarketScheduler(database).Next(DateTimeOffset.UtcNow));
            if(target is null){status.Text="请导入商品目录或确认识别候选商品";return;}
            await Run(async(w,t)=>{var message=await w.NavigateAsync(target.Item,new ImageFileCapture(lastImage),new SimulatedMouseExecutor(),t);await Dispatcher.InvokeAsync(()=>status.Text=message);});
        });
        var stop=new Button{Content="停止 / 紧急暂停",Background=Brushes.DarkRed};stop.Click+=(_,_)=>{workflow?.Stop();cancel?.Cancel();trading.Stop();navigation.Stop();autoBuy.Stop();status.Text="已请求停止；正在取消采集、导航和真实交易";};actions.Children.Add(stop);
        header.Children.Add(status);header.Children.Add(coverage);header.Children.Add(seedStatus);
        var tabs=new TabControl{Margin=new Thickness(0,12,0,0),Background=Background,Foreground=Foreground};panel.Children.Add(tabs);
        var visionGrid=new Grid();visionGrid.ColumnDefinitions.Add(new(){Width=new GridLength(2,GridUnitType.Star)});visionGrid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        visionGrid.Children.Add(preview);Grid.SetColumn(detail,1);visionGrid.Children.Add(detail);
        object TabHeader(string text)=>new Border{Background=new SolidColorBrush(Color.FromRgb(35,53,68)),Padding=new Thickness(8,3,8,3),Child=new TextBlock{Text=text,Foreground=Brushes.AliceBlue}};
        tabs.Items.Add(new TabItem{Header=TabHeader("识别结果与报价"),Content=visionGrid,Background=Background});
        var catalogPanel=new DockPanel();var catalogActions=new WrapPanel();DockPanel.SetDock(catalogActions,Dock.Top);catalogPanel.Children.Add(catalogActions);catalogPanel.Children.Add(catalog);
        catalog.Columns.Add(new DataGridTextColumn{Header="商品名称",Binding=new Binding(nameof(CatalogItem.Name)),Width=new DataGridLength(2,DataGridLengthUnitType.Star)});
        catalog.Columns.Add(new DataGridTextColumn{Header="网站/人工分类",Binding=new Binding(nameof(CatalogItem.Category)),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        catalog.Columns.Add(new DataGridCheckBoxColumn{Header="名称已入目录",Binding=new Binding(nameof(CatalogItem.Confirmed)),Width=120});
        catalog.Columns.Add(new DataGridTextColumn{Header="最近实时巡查",Binding=new Binding(nameof(CatalogItem.LastVisited)){StringFormat="yyyy-MM-dd HH:mm",TargetNullValue="尚未巡查"},Width=180});
        Add(catalogActions,"导入确认商品目录",async()=>{
            var dialog=new OpenFileDialog{Filter="商品目录 JSON|*.json"};if(dialog.ShowDialog()!=true)return;
            try{var items=JsonSerializer.Deserialize<CatalogItem[]>(await File.ReadAllTextAsync(dialog.FileName))??[];await Task.Run(()=>{foreach(var item in items)database.UpsertItem(item);});await Refresh();}
            catch(Exception ex){status.Text=ex.Message;}
        });
        Add(catalogActions,"确认所选商品名称",async()=>{
            if(catalog.SelectedItem is CatalogItem item){await Task.Run(()=>database.UpsertItem(item with{Confirmed=true}));await Refresh();}
        });
        var stats=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(6),Foreground=Brushes.AliceBlue};DockPanel.SetDock(stats,Dock.Bottom);catalogPanel.Children.Insert(1,stats);
        catalog.SelectionChanged+=async(_,_)=>{if(catalog.SelectedItem is CatalogItem item){
            try {
                var data=await Task.Run(()=>(database.Statistics(item.Id,"market-card"),database.References(item.Id)));
                if(closed||!ReferenceEquals(catalog.SelectedItem,item))return;var s=data.Item1;
                stats.Text=$"实时卡片报价 · 样本 {s.Samples} · 最低 {s.Minimum} · 中位 {s.Median} · P25 {s.P25} · P75 {s.P75} · 最高 {s.Maximum}"
                    +string.Join("",data.Item2.Select(r=>$"\n暗区情报站初始参考价：{r.Price?.ToString()??"未知"} · 页面读取：{r.FetchedAt.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm}（国服时间） · 源报价时间：{r.SourceUpdatedAt?.ToString("O")??"未知"}\n{r.Url}\n转售资格与兑换用途待游戏验证；参考价不参与实时统计或交易决策。"));
            }catch(Exception ex){stats.Text="读取统计失败："+ex.Message;}
        }};
        catalog.Background=Background;catalog.RowBackground=Background;catalog.AlternatingRowBackground=new SolidColorBrush(Color.FromRgb(24,40,52));catalog.Foreground=Foreground;
        var columnHeader=new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));columnHeader.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(35,53,68))));columnHeader.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.AliceBlue));catalog.ColumnHeaderStyle=columnHeader;
        tabs.Items.Add(new TabItem{Header=TabHeader("商品目录与覆盖率"),Content=catalogPanel,Background=Background});
        tabs.Items.Add(new TabItem{Header=TabHeader("兑换分析与模拟账本"),Content=trading,Background=Background});
        tabs.Items.Add(new TabItem{Header=TabHeader("视觉导航调试"),Content=navigation,Background=Background});
        tabs.Items.Add(new TabItem{Header=TabHeader("钥匙按需采购"),Content=new KeyProcurementPanel(Path.Combine(root,"key-procurement.json")),Background=Background});
        tabs.Items.Add(new TabItem{Header=TabHeader("统一利润建议"),Content=profits,Background=Background});
        tabs.Items.Add(new TabItem{Header=TabHeader("单笔自动买入/兑换"),Content=new ScrollViewer{Content=autoBuy,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},Background=Background});
        foreach(var button in workButtons)button.IsEnabled=false;
        Loaded+=(_,_)=>Initialization=InitializeSeedAsync();
        Closed+=(_,_)=>{closed=true;workflow?.Stop();cancel?.Cancel();trading.Stop();navigation.Stop();autoBuy.Stop();};
    }
    private async Task InitializeSeedAsync() {
            try {
                var imported=await Task.Run(()=>{var result=database.ImportSeed(MarketDatabase.BundledSeedPath);foreach(var issue in database.ImportKnownAliases(Path.Combine(AppContext.BaseDirectory,"MarketSeed","reference-profiles.json")))database.Log(null,null,issue);database.BackfillRecentProfitPrices(DateTimeOffset.UtcNow);return result;});
                seedStatus.Text=$"暗区情报站 · 初始参考目录 {imported.MappedItems} 项 / 参考价 {imported.ReferencePrices} 项 · {(imported.AlreadyImported?"已载入原有初始数据":"首次导入完成")} · 转售资格待验证";
            }catch(Exception ex){seedStatus.Text="初始参考目录未导入："+ex.Message;}
            finally{if(!closed){foreach(var button in workButtons)button.IsEnabled=true;await Refresh();}}
    }
    internal void SelectCatalogPreview() {
        if(Content is DockPanel panel)panel.Children.OfType<TabControl>().Single().SelectedIndex=1;
        catalog.SelectedItem=catalog.Items.OfType<CatalogItem>().FirstOrDefault();
    }
    internal NavigationDebugPanel NavigationDebug=>navigation;
    internal void SelectNavigationPreview(){if(Content is DockPanel panel)panel.Children.OfType<TabControl>().Single().SelectedIndex=3;}
    internal void SelectTradingPreview(){if(Content is DockPanel panel)panel.Children.OfType<TabControl>().Single().SelectedIndex=2;}
    private void Add(Panel panel,string title,Func<Task> action) {
        var button=new Button{Content=title,Margin=new Thickness(3)};button.Click+=async(_,_)=>{try{await action();}catch(Exception ex){status.Text=ex.Message;}};
        workButtons.Add(button);panel.Children.Add(button);
    }
    private async Task Run(Func<MarketWorkflow,CancellationToken,Task> operation) {
        if(cancel is not null||trading.Busy||navigation.Busy||autoBuy.Busy){status.Text="等待当前任务结束";return;}
        if(!File.Exists(config.Text)){status.Text="请先选择模型配置文件";return;}
        var configPath=config.Text;cancel=new();var token=cancel.Token;
        foreach(var b in workButtons)b.IsEnabled=false;
        trading.SetCollecting(true);navigation.SetCollecting(true);var analysis=trading.Analysis;
        status.Text="正在初始化；采集游戏时请在4秒内切到游戏窗口。停止按钮始终可用。";
        try {
            await Task.Run(async()=>{
                await using var vision=new PythonVisionPipeline(configPath);
                workflow=new(vision,database,Path.Combine(root,"evidence"),analysis);
                await operation(workflow,token);
            },token);
        }catch(OperationCanceledException){status.Text="已停止，音频功能继续运行";}
        catch(Exception ex){status.Text="已暂停："+ex.Message;await Task.Run(()=>database.Log(null,null,ex.ToString()));}
        finally{workflow=null;cancel.Dispose();cancel=null;foreach(var b in workButtons)b.IsEnabled=true;trading.SetCollecting(false);navigation.SetCollecting(false);if(!closed)await Refresh();}
    }
    private async Task Refresh() {
        var data=await Task.Run(()=>(database.Catalog(),database.Coverage(DateTimeOffset.UtcNow.AddHours(-24))));
        if(closed)return;catalog.ItemsSource=data.Item1;coverage.Text=$"24小时实时覆盖 {data.Item2.Visited}/{data.Item2.Known} · 待确认商品 {data.Item2.Candidates} · 异常/诊断记录 {data.Item2.Errors}";
    }
    internal void Show(CollectionUpdate update,string? path) {
        if(closed)return;
        var p=update.Perception;lastImage=path??lastImage;
        trading.Observe(update);profits.Refresh();
        status.Text=$"{(path is null?"实时采集":"历史回放")} · {p.Page} · YOLO {p.Vision.YoloMs:F0}ms / OCR {p.Vision.OcrMs:F0}ms / 总计 {p.Vision.TotalMs:F0}ms · 新增报价 {update.Inserted}";
        detail.Text=string.Join("\n",p.Quotes.Select(q=>$"{q.ItemName}\n{q.Price} × {q.Quantity?.ToString()??"未知数量"} · 置信度 {q.Confidence:P1} · {q.Context}"))
            +"\n\n"+string.Join("\n",p.Issues)+"\n"+p.Reason+"\n\nOCR：\n"+string.Join("\n",p.Vision.Texts.Select(t=>$"{t.Label} ({t.Confidence:P0})"));
        var imagePath=path??update.ScreenshotPath;
        if(imagePath is not null){lastImage=imagePath;preview.Source=RenderPreview(imagePath,p.Vision);}
        coverage.Text=$"24小时实时覆盖 {update.Coverage.Visited}/{update.Coverage.Known} · 待确认商品 {update.Coverage.Candidates} · 下一目标 {update.Next?.Item.Name??"未安排"}";
    }
    internal static ImageSource RenderPreview(string path,VisionResult result) {
        var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.UriSource=new Uri(Path.GetFullPath(path));bitmap.EndInit();bitmap.Freeze();
        var drawing=new DrawingGroup();using(var dc=drawing.Open()) {
            dc.DrawImage(bitmap,new Rect(0,0,result.Width,result.Height));
            foreach(var t in result.Regions.Where(t=>t.Confidence>=.6))dc.DrawRectangle(null,new Pen(Brushes.Lime,3),new Rect(t.Bounds.X*result.Width,t.Bounds.Y*result.Height,t.Bounds.Width*result.Width,t.Bounds.Height*result.Height));
            foreach(var t in result.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence))dc.DrawRectangle(null,new Pen(Brushes.DeepSkyBlue,1),new Rect(t.Bounds.X*result.Width,t.Bounds.Y*result.Height,t.Bounds.Width*result.Width,t.Bounds.Height*result.Height));
        }drawing.Freeze();return new DrawingImage(drawing);
    }
}
