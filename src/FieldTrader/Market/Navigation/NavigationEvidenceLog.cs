using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldTrader.Market;
namespace FieldTrader.Navigation;
public sealed record NavigationSnapshot(PageObservation? Page,ActionBatch? Plan,PageObservation? Before,PageObservation? After,Verification? Verification,NavigationEvent? Event,Dictionary<string,double>? LatestTimings=null);
public sealed class NavigationEvidenceLog(string directory,Action<NavigationSnapshot>? publish=null) : INavigationLog,INavigationTiming,INavigationObserver {
    public static readonly JsonSerializerOptions Json=new(){WriteIndented=true,Converters={new JsonStringEnumConverter()}};
    private readonly object sync=new();
    public readonly Dictionary<string,List<double>> Timings=[];
    public readonly List<NavigationEvent> Events=[];
    private PageObservation? page,before,after;private ActionBatch? plan;private Verification? verified;
    public void Measure(string stage,double milliseconds){lock(sync){if(!Timings.TryGetValue(stage,out var values))Timings[stage]=values=[];values.Add(milliseconds);}}
    public void Append(NavigationEvent entry){lock(sync){Directory.CreateDirectory(directory);Events.Add(entry);File.AppendAllText(Path.Combine(directory,"events.jsonl"),JsonSerializer.Serialize(entry)+"\n");publish?.Invoke(new(page,plan,before,after,verified,entry,Timings.ToDictionary(k=>k.Key,k=>k.Value.LastOrDefault())));}}
    public void Observation(PageObservation observation) {
        var timer=Stopwatch.StartNew();lock(sync){page=observation;Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,observation.Screen.Frame.Id+"-observation.json"),JsonSerializer.Serialize(new{CoordinateSystem="NormalizedOriginalClientPixels; Click=PhysicalDesktopPixels",Observation=observation},Json));
            publish?.Invoke(new(page,plan,before,after,verified,null,Timings.ToDictionary(k=>k.Key,k=>k.Value.LastOrDefault())));
        }Measure("EvidencePersist",timer.Elapsed.TotalMilliseconds);
    }
    public void Plan(ActionBatch batch){lock(sync){plan=batch;File.WriteAllText(Path.Combine(directory,batch.Source.Screen.Frame.Id+"-plan.json"),JsonSerializer.Serialize(batch,Json));publish?.Invoke(new(page,plan,before,after,verified,null,Timings.ToDictionary(k=>k.Key,k=>k.Value.LastOrDefault())));}}
    public void Verified(PageObservation pre,PageObservation post,NavigationStep step,Verification result){lock(sync){before=pre;after=post;verified=result;
        File.AppendAllText(Path.Combine(directory,"verification.jsonl"),JsonSerializer.Serialize(new{Step=step.Id,BeforeFrame=pre.Screen.Frame.Id,AfterFrame=post.Screen.Frame.Id,Before=pre.Screen.Frame.ScreenshotPath,After=post.Screen.Frame.ScreenshotPath,Result=result})+"\n");publish?.Invoke(new(page,plan,before,after,verified,null,Timings.ToDictionary(k=>k.Key,k=>k.Value.LastOrDefault())));}}
    public void Complete(NavigationResult result)=>File.WriteAllText(Path.Combine(directory,"session.json"),JsonSerializer.Serialize(new{Result=result,Timings,Events},Json));
}
