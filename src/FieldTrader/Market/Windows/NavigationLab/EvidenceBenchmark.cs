using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FieldTrader.Market;
using FieldTrader.Navigation;

namespace FieldTrader.NavigationLab;

// Pure-algorithm timings from the already captured real GUI evidence, separately labeled.
// This is not a replacement for SendInput acceptance and does not invent live provenance.
internal static class EvidenceBenchmark {
    internal static void Run(string evidenceDirectory,string output){
        var resolver=new VisualTargetResolver();var classifier=new LabClassifier();var rows=new List<object>();
        foreach(var file in Directory.EnumerateFiles(evidenceDirectory,"*-vision.json",SearchOption.AllDirectories).Order()){
            using var doc=JsonDocument.Parse(File.ReadAllText(file));var root=doc.RootElement;
            var s=root.GetProperty("Screen").Deserialize<CapturedScreen>()!;var v=root.GetProperty("Vision").Deserialize<VisionResult>()!;
            var name=root.GetProperty("StepId").GetString()!;var q=Suite.Query(name);var page=classifier.Classify(s,v);
            var timer=Stopwatch.StartNew();var initial=resolver.Resolve(page,q);double cold=timer.Elapsed.TotalMilliseconds;
            var warm=new double[20];
            for(int n=0;n<20;n++){timer.Restart();var found=resolver.Resolve(page,q);warm[n]=timer.Elapsed.TotalMilliseconds;if(found.Length!=1)throw new InvalidDataException("Benchmark target is not unique: "+file);}
            rows.Add(new{Evidence=Path.GetFullPath(file),s.Frame.Id,Target=name,InitialMs=cold,Warm20Ms=warm,InitialCandidates=initial.Length});
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,JsonSerializer.Serialize(new{Kind="Offline target-fusion microbenchmark on persisted real GUI screenshots/fixture detections",NativeInput=false,ModelInference=false,ModelInitializationMs=(double?)null,Rows=rows},new JsonSerializerOptions{WriteIndented=true}));
    }
}
