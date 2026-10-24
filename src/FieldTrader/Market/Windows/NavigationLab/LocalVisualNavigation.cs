using System.IO;
using System.Text.Json;
using System.Windows;
using FieldTrader.Market;
using FieldTrader.Navigation;
namespace FieldTrader.NavigationLab;
internal sealed class LocalSimulationInput(LabWindow window) : IInputController {
    public bool IsSimulation=>true;
    public Task ExecuteAsync(PlannedAction action,CapturedScreen source,CancellationToken token){token.ThrowIfCancellationRequested();NavigationSafety.Check(action.Step,action.Target);return window.InvokeSimulated(action.Target.Name);}
}
public sealed record LocalSessionResult(NavigationResult Result,int NativeClicks,int WrongClicks,int SimulatedClicks,string Recognizer,string Output);
public static class LocalVisualNavigation {
    public static async Task<LocalSessionResult> Run(ExecutionMode mode,string output,NavigationControl control,Action<NavigationSnapshot>? publish=null,CancellationToken token=default) {
        Directory.CreateDirectory(output);var window=new LabWindow(output,1);window.Show();window.BringLocalForeground();
        try {
            await window.Prepare();window.Running=true;window.ActiveControl=control;await window.UI(()=>window.RouteMode=true);using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,control.EmergencyToken);await Task.Delay(350,linked.Token);
            var capture=new LocalScreenSource(window.Hwnd,Path.Combine(output,"screens"));var vision=new MarkerVision();var log=new NavigationEvidenceLog(output,publish);
            IInputController input=mode==ExecutionMode.Real?new RealInput(window):new LocalSimulationInput(window);
            var steps=new[]{Suite.Step("Next",1,after:"Detail",dynamic:true),Suite.Step("C",2,before:"Detail",after:"Warehouse",dynamic:true),Suite.Step("D",3,before:"Warehouse",after:"Contact",dynamic:true)};
            var task=new NavigationTask("integrated-local-"+Guid.NewGuid().ToString("N"),steps,mode,BatchMode.DynamicPage,MaxBatchTime:TimeSpan.FromSeconds(15),ActionInterval:TimeSpan.FromMilliseconds(100));
            var result=await Task.Run(()=>new NavigationExecutor(capture,vision,new LabClassifier(),input,log).RunAsync(task,control,token));
            log.Complete(result);
            var report=await window.UI(()=>new LocalSessionResult(result,window.Received.Count(r=>r.Kind=="click"),window.WrongClicks+window.BackgroundClicks,window.Received.Count(r=>r.Kind=="simulated-click"),"Adapted persisted screenshot pixel fixture; NOT YOLO/OCR",output));
            File.WriteAllText(Path.Combine(output,"local-result.json"),JsonSerializer.Serialize(report,NavigationEvidenceLog.Json));return report;
        }finally{window.Running=false;window.ActiveControl=null;window.Close();}
    }
}
