using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using FieldTrader.Navigation;
using FieldTrader.NavigationLab;
namespace FieldTrader.Market;

public static class NativeMouseValidation {
 public static async Task Run(string output) {
  Directory.CreateDirectory(output);Application.Current.ShutdownMode=ShutdownMode.OnExplicitShutdown;
  int clicks=0,downs=0,ups=0;var button=new Button{Content="FieldTrader mouse test — one local click"};
  button.Click+=(_,_)=>clicks++;button.PreviewMouseLeftButtonDown+=(_,_)=>downs++;button.PreviewMouseLeftButtonUp+=(_,_)=>ups++;
  var window=new Window{Title="FieldTrader Native Mouse Check — LOCAL ONLY",Width=640,Height=360,Content=button,WindowStartupLocation=WindowStartupLocation.CenterScreen};
  IntPtr handle=IntPtr.Zero;
  try {
   window.Show();handle=new WindowInteropHelper(window).Handle;LocalNavigationWindows.Register(handle);window.Activate();
   await Task.Delay(250);
   var capture=new LocalScreenSource(handle,Path.Combine(output,"screens"));
   await Task.Run(async()=>{
    await NavigationInputGate.Shared.WaitAsync();NavigationInputGate.Enter();
    try{
     var screen=await capture.CaptureAsync(default);var bounds=new Region(.4,.4,.2,.2);
     var target=new VisualTarget("Next","next_page",bounds,CoordinateTransform.ToScreen(bounds,screen),1,screen.Frame.Id,screen.Frame.CapturedAt,screen.ContentHash);
     var step=new NavigationStep("mouse-native-check","Home",new("Next","next_page",new(0,0,1,1),"Home",new(0,0,1,1)),InputOperation.Click,"Home",TimeSpan.FromSeconds(5));
     await new WindowsInputController(handle).ExecuteAsync(new(0,step,target),screen,default);
    }finally{NavigationInputGate.Exit();NavigationInputGate.Shared.Release();}
   });
   await Task.Delay(200);
   bool passed=clicks==1&&downs==1&&ups==1;
   File.WriteAllText(Path.Combine(output,"native-mouse-check.json"),JsonSerializer.Serialize(new{Passed=passed,Mode="RealWindowsSendInputToOwnedTestWindow",GameInputs=0,Clicks=clicks,ButtonDown=downs,ButtonUp=ups},TradingAnalysis.JsonOptions));
   if(!passed)throw new InvalidOperationException("Local window did not receive exactly one native click");
  }catch(Exception ex){File.WriteAllText(Path.Combine(output,"native-mouse-error.txt"),ex.ToString());throw;}
  finally{if(handle!=IntPtr.Zero)LocalNavigationWindows.Remove(handle);window.Close();}
 }
}
