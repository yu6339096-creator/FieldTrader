using System.Text.Json;
using System.Text.Json.Serialization;
using FieldTrader.Market;

var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
if(args.Length==4&&args[0]=="--visual-navigation-replay"){await VisualNavigationValidation.Replay(args[1],args[2],args[3]);return;}
if(args.Length==3&&args[0]=="--reparse"){MarketPriorityValidation.Reparse(args[1],args[2]);return;}
if(args.Length==2&&args[0]=="--priority-test"){MarketPriorityValidation.Run(args[1]);return;}
if(args.Length==2&&args[0]=="--stage-cd-test"){await MarketCdValidation.Run(args[1]);Console.WriteLine("Stage C/D simulation checks passed");return;}
if(args.Length==4&&args[0]=="--cd-vision"){await MarketCdValidation.Replay(args[1],args[2],args[3]);Console.WriteLine("C/D screenshot replay complete");return;}
if(args.Length==3 && args[0]=="--seed") {var db=new MarketDatabase(args[2]);Console.WriteLine(JsonSerializer.Serialize(db.ImportSeed(args[1]),options));return;}
if(args.Length==2 && args[0]=="--stage-ab-test") {await MarketStageValidation.Run(args[1]);Console.WriteLine("Stage A/B checks passed");return;}
if(args.Length==4 && args[0]=="--vision") {await MarketStageValidation.AnalyzeFolder(args[1],args[2],args[3]);Console.WriteLine("Vision replay completed");return;}
if (args.Length == 1 && args[0] == "--test") {
    await MarketValidation.Run();
    Console.WriteLine("Market core checks passed; no game input or audio devices used.");
    return;
}
if (args.Length != 2) throw new ArgumentException("Usage: MarketReplay <replay.json> <output-directory> | --test");
var replay = JsonSerializer.Deserialize<Replay>(File.ReadAllText(args[0]), options) ?? throw new InvalidDataException("Empty replay");
Directory.CreateDirectory(args[1]);
var journal = new MarketJournal(Path.Combine(args[1], "observations.jsonl"));
var executor = new MarketExecutor();
var events = new List<SimulationEvent>();
foreach (var observation in replay.Observations) journal.Append(observation);
foreach (var entry in replay.Steps) {
    var before = EvidenceFusion.Fuse(entry.Before.Frame, entry.Before.Yolo, entry.Before.Ocr);
    var after = EvidenceFusion.Fuse(entry.After.Frame, entry.After.Yolo, entry.After.Ocr);
    var result = await executor.SimulateAsync(entry.Step, before, after, replay.Budget, replay.OcrRemainingListings, replay.Now, CancellationToken.None);
    events.Add(result);
    if (result.State is TradeState.Unknown or TradeState.Stopped) break;
}
File.WriteAllText(Path.Combine(args[1], "simulation.json"), JsonSerializer.Serialize(new {
    Mode = "ReplayOnly", NativeInput = false, Events = events, executor.ReservedBudget, executor.ReservedListings,
    History = journal.Snapshot().Where(r => r.Kind == "price").Select(r => r.ItemId).Distinct()
        .Select(id => new { ItemId = id, Statistics = journal.Statistics(id, DateTimeOffset.MinValue).ToString() })
}, options));
public sealed record EngineFrame(Frame Frame, Detection Yolo, Detection Ocr);
public sealed record ReplayStep(TradeStep Step, EngineFrame Before, EngineFrame After);
public sealed record Replay(DateTimeOffset Now, decimal Budget, int? OcrRemainingListings, MarketRecord[] Observations, ReplayStep[] Steps);
