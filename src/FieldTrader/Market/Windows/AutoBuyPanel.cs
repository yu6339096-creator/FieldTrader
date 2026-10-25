using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Data;
using System.Windows.Media;
namespace FieldTrader.Market;
public sealed class AutoBuyPanel:StackPanel {
 private readonly CheckBox enabled=new(){Content="启用真实单笔自动执行（使用游戏币）"};
 private readonly ComboBox mode=new(){ItemsSource=new[]{"拉里100D：补齐并兑换1件","市场购买：按当前详情数量"},SelectedIndex=0};
 private readonly TextBox item=new(){Text="100D战地医疗包"},quantity=new(){Text="1"},budget=new(){Text="0"};
 private readonly TextBlock status=new(){Text="先在游戏中打开目标详情，再设置支出上限。每轮结束自动重置，再次开始会重新识别和核价。",TextWrapping=TextWrapping.Wrap};
 private readonly Button start=new(){Content="4秒后开始单笔自动执行"};
 private CancellationTokenSource? cancellation;private int armed;
 private HwndSource? hotkeySource;private bool hotkeyReady;
 private string stopKey="未就绪";
 private readonly TextBlock hotkeyStatus=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,4)};
 private readonly Button stopButton=new(){Content="立即停止"};
 private const int StopHotkey=0x4BFF;
 [DllImport("user32.dll",SetLastError=true)]private static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
 [DllImport("user32.dll")]private static extern bool UnregisterHotKey(IntPtr hwnd,int id);
 private IntPtr Hotkey(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled){if(message==0x312&&wParam.ToInt32()==StopHotkey){Stop();status.Text=stopKey+"急停已触发；本轮结束后重置，不自动重试。";handled=true;}return IntPtr.Zero;}
 private void BindStopKey(Func<uint,uint,bool> register) {
  hotkeyReady=false;stopKey="未就绪";
  foreach(var candidate in new[]{(0u,0x77u,"F8"),(3u,0x77u,"Ctrl+Alt+F8"),(3u,0x78u,"Ctrl+Alt+F9")}) {
   if(!register(candidate.Item1|0x4000,candidate.Item2))continue;
   hotkeyReady=true;stopKey=candidate.Item3;break;
  }
  start.IsEnabled=hotkeyReady&&!Busy;
  hotkeyStatus.Text=hotkeyReady?"全局急停："+stopKey+(stopKey=="F8"?"":"（F8被占用，已自动改用备用快捷键）"):"F8及备用急停键均不可用；请关闭旧版程序后点击重试。";
  stopButton.Content="立即停止（"+stopKey+"）";
 }
 private void ReleaseHotkey(){if(hotkeySource is not null){if(hotkeyReady)UnregisterHotKey(hotkeySource.Handle,StopHotkey);hotkeySource.RemoveHook(Hotkey);}hotkeyReady=false;hotkeySource=null;}
 private void InitializeHotkey(){
  if(hotkeyReady&&hotkeySource is not null)return;
  ReleaseHotkey();var window=Window.GetWindow(this);if(window is null)return;
  hotkeySource=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);if(hotkeySource is null)return;
  hotkeySource.AddHook(Hotkey);BindStopKey((mod,key)=>RegisterHotKey(hotkeySource.Handle,StopHotkey,mod,key));
 }
 private void ApplyMode(){bool exchange=mode.SelectedIndex==0;quantity.IsReadOnly=exchange;item.IsReadOnly=exchange;if(exchange){quantity.Text="1";item.Text="100D战地医疗包";}}
 private AutoBuyKind SelectedKind()=>mode.SelectedIndex switch{0=>AutoBuyKind.Larry100DExchange,1=>AutoBuyKind.MarketPurchase,_=>throw new InvalidOperationException("请选择有效操作")};
 public bool Busy=>cancellation is not null;
 public void Stop(){Interlocked.Exchange(ref armed,0);cancellation?.Cancel();}
 public AutoBuyPanel(string directory,Func<string> config,Func<bool> otherBusy,Action<bool> busyChanged) {
  Margin=new Thickness(12);
  // Global ComboBox template binds Name; these items are strings and require a direct binding.
  var text=new FrameworkElementFactory(typeof(TextBlock));text.SetBinding(TextBlock.TextProperty,new Binding());mode.ItemTemplate=new DataTemplate{VisualTree=text};
  mode.SelectionChanged+=(_,_)=>ApplyMode();ApplyMode();
  Children.Add(new TextBlock{Text="真实单笔买入 / 补齐 / 兑换",FontSize=22});
  using(var identity=System.Security.Principal.WindowsIdentity.GetCurrent())Children.Add(new TextBlock{Text=new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)?"管理员模式：已启用":"管理员模式：未启用，请从新版安装快捷方式启动",TextWrapping=TextWrapping.Wrap});
  Children.Add(new TextBlock{Text="OCR门槛75%；Windows SendInput控制真实鼠标。从当前详情页开始，核价后自动点击。兑换100D×1；市场数量1—5。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,10)});
  void Field(string title,UIElement element){Children.Add(new TextBlock{Text=title,Margin=new Thickness(0,6,0,2)});Children.Add(element);}
  Field("操作",mode);Field("商品完整名称",item);Field("购买数量（兑换固定1）",quantity);Field("本轮游戏币支出上限（必须填写）",budget);
  Children.Add(enabled);Children.Add(start);Children.Add(status);
  Children.Add(hotkeyStatus);stopButton.Click+=(_,_)=>Stop();Children.Add(stopButton);
  var retryHotkey=new Button{Content="重试急停快捷键"};retryHotkey.Click+=(_,_)=>{if(!Busy)InitializeHotkey();};Children.Add(retryHotkey);
  var inspectInput=new Button{Content="检查鼠标环境（4秒后读取，不移动、不点击）"};
  inspectInput.Click+=async(_,_)=>{
   if(Busy||otherBusy())return;
   inspectInput.IsEnabled=false;start.IsEnabled=false;busyChanged(true);
   status.Text="请在4秒内切回游戏。此次只读取鼠标和桌面环境，不执行交易。";
   try {
    var report=await Task.Run(async()=>{await Task.Delay(4000);return WindowsInputController.InspectEnvironment();});
    Directory.CreateDirectory(directory);var file=Path.Combine(directory,"mouse-environment-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".txt");
    File.WriteAllText(file,report);status.Text="鼠标环境检查完成（没有移动或点击）\n"+report+"\n记录："+file;
   }catch(Exception ex){status.Text="环境检查失败："+ex.Message;}
   finally{inspectInput.IsEnabled=true;start.IsEnabled=hotkeyReady;busyChanged(false);}
  };Children.Add(inspectInput);
  Loaded+=(_,_)=>InitializeHotkey();
  Unloaded+=(_,_)=>{Stop();ReleaseHotkey();};
  enabled.Unchecked+=(_,_)=>Stop();
  start.Click+=async(_,_)=>{
   if(Busy||otherBusy()){status.Text="请先停止其他采集或导航任务。";return;}
   if(!hotkeyReady){status.Text="全局急停尚未就绪。";return;}
   if(enabled.IsChecked!=true){status.Text="尚未启用真实执行。";return;}
   ApplyMode();
   if(!int.TryParse(quantity.Text,out int qty)||!decimal.TryParse(budget.Text,out decimal cap)||cap<=0){status.Text="请填写有效数量和正数支出上限。";return;}
   var request=new AutoBuyRequest(SelectedKind(),item.Text.Trim(),qty,cap);var path=config();
   if(!File.Exists(path)){status.Text="缺少有效模型配置。";return;}
   cancellation=new();var token=cancellation.Token;Interlocked.Exchange(ref armed,1);start.IsEnabled=false;busyChanged(true);
   status.Text="4秒内切回游戏当前详情页。程序只执行这一笔，关闭开关或全局急停可中止。";
   try {
    var result=await Task.Run(async()=>{
     for(int seconds=4;seconds>0;seconds--){int remaining=seconds;await Dispatcher.InvokeAsync(()=>status.Text=$"{remaining}秒后开始，请切回游戏详情页。全局急停：{stopKey}");await Task.Delay(1000,token);}
     await using var pipeline=new PythonVisionPipeline(path);
     var runner=new AutoBuyRunner(new GameScreenCapture(Path.Combine(directory,"screens")),pipeline,new GameTradeInput(directory),directory,message=>Dispatcher.BeginInvoke(()=>status.Text=message));
     return await runner.Run(request,()=>Volatile.Read(ref armed)==1,token);
    },token);
    status.Text=$"{result.State}：{result.Reason}\n实际点击 {result.Clicks} 次；本轮支出报价 {result.ReservedQuote}；已核对实际支出 {result.ActualCash?.ToString()??"未知"}\n证据：{result.EvidenceDirectory}\n本轮结束，运行状态已重置。再次开始会重新识别和核价。";
   }catch(Exception ex){status.Text="已停止："+ex.Message;}
   finally{Stop();cancellation.Dispose();cancellation=null;enabled.IsChecked=false;start.IsEnabled=hotkeyReady;busyChanged(false);}
  };
 }
 public static void ValidateUi(string output) {
  Directory.CreateDirectory(output);var p=new AutoBuyPanel(output,()=>"",()=>false,_=>{});var checks=new List<string>();
  void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);checks.Add(name);}
  int attempts=0;p.BindStopKey((_,_)=>++attempts==2);
  Check(p.stopKey=="Ctrl+Alt+F8"&&p.start.IsEnabled,"F8冲突后自动采用备用键并恢复开始按钮");
  p.BindStopKey((_,_)=>false);Check(!p.start.IsEnabled,"所有急停键冲突时保持禁用");
  p.BindStopKey((_,_)=>true);Check(p.stopKey=="F8"&&p.start.IsEnabled,"重试成功后恢复开始按钮");
  p.quantity.Text="3";p.ApplyMode();Check(p.quantity.Text=="1"&&p.quantity.IsReadOnly&&p.item.IsReadOnly,"兑换数量固定1且商品不可误改");
  Check(p.SelectedKind()==AutoBuyKind.Larry100DExchange,"拉里选项发送兑换请求而非市场购买请求");
  p.mode.SelectedIndex=1;Check(!p.quantity.IsReadOnly&&!p.item.IsReadOnly,"市场购买允许设置数量和商品");
  Check(p.SelectedKind()==AutoBuyKind.MarketPurchase,"市场选项发送市场购买请求而非兑换请求");
  p.mode.SelectedIndex=0;p.budget.Text="40000";p.BindStopKey((_,_)=>++attempts>2);
  var border=new Border{Background=new SolidColorBrush(Color.FromRgb(15,26,35)),Child=p,Width=1060,Height=800};border.Measure(new Size(1060,800));border.Arrange(new Rect(0,0,1060,800));border.UpdateLayout();
  var visible=FindText(p.mode).ToArray();Check(visible.Contains("拉里100D：补齐并兑换1件"),"实际控件树显示选中操作的文字");
  var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(1060,800,96,96,PixelFormats.Pbgra32);bitmap.Render(border);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var f=File.Create(Path.Combine(output,"auto-buy-panel.png")))png.Save(f);
  File.WriteAllText(Path.Combine(output,"ui-checks.json"),System.Text.Json.JsonSerializer.Serialize(new{Checks=checks,Passed=true,NativeGameInput=false},TradingAnalysis.JsonOptions));
 }
 private static IEnumerable<string> FindText(DependencyObject root){if(root is TextBlock t)yield return t.Text;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var text in FindText(VisualTreeHelper.GetChild(root,i)))yield return text;}
}
