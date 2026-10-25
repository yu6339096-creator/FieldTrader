using System.IO;
using System.Text.Json;
using FieldTrader.Navigation;
namespace FieldTrader.Market;
public static class MaterialMarketValidation {
    public static async Task Replay(string config,string images,string output) {
        Directory.CreateDirectory(output);await using var pipeline=new PythonVisionPipeline(config);var rows=new List<object>();
        foreach(var file in Directory.GetFiles(images,"*.png").Order()) {
            var s=await new ImageFileCapture(file).CaptureAsync(default);var v=await pipeline.AnalyzeAsync(s,default);s=s with{Width=v.Width,Height=v.Height};
            var row=Inspect(s,v,Path.GetFileName(file));rows.Add(row);
            File.WriteAllText(Path.Combine(output,Path.GetFileNameWithoutExtension(file)+".json"),JsonSerializer.Serialize(row,NavigationEvidenceLog.Json));Console.WriteLine(Path.GetFileName(file));
        }
        File.WriteAllText(Path.Combine(output,"replay.json"),JsonSerializer.Serialize(new{NativeInput=false,Purchases=0,Rows=rows},NavigationEvidenceLog.Json));
    }
    public static void Reparse(string source,string output) {
        Directory.CreateDirectory(output);using var doc=JsonDocument.Parse(File.ReadAllText(source));
        var rows=doc.RootElement.GetProperty("Rows").EnumerateArray().Select(r=>Inspect(r.GetProperty("Observation").Deserialize<PageObservation>(NavigationEvidenceLog.Json)!.Screen,r.GetProperty("RawVision").Deserialize<VisionResult>(NavigationEvidenceLog.Json)!,r.GetProperty("Image").GetString()!)).ToArray();
        File.WriteAllText(Path.Combine(output,"replay.json"),JsonSerializer.Serialize(new{NativeInput=false,Purchases=0,Rows=rows},NavigationEvidenceLog.Json));
    }
    static object Inspect(CapturedScreen screen,VisionResult raw,string image) {
        var p=new MaterialMarketClassifier().Classify(screen,raw);var plans=new List<object>();var planner=new FieldTrader.Navigation.NavigationPlanner(new());
        var steps=MaterialMarketTargets.Items.Select(i=>MaterialMarketRoutes.Next(p,i)).Where(s=>s is not null).Cast<NavigationStep>().ToList();
        if(p.Page=="Purchase") {steps.Add(MaterialMarketRoutes.Close());foreach(var item in MaterialMarketTargets.Items)if(MaterialMarketRoutes.Quantity(p,item,5) is {} quantity)steps.Add(quantity);}
        foreach(var step in steps.DistinctBy(s=>s.Id)) {
            try{plans.Add(new{Step=step,Plan=planner.Locate(p,step,0),Error=(string?)null});}
            catch(NavigationEvidenceException e){plans.Add(new{Step=step,Plan=(PlannedAction?)null,Error=e.Message});}
        }
        var quote=MaterialPurchaseReader.Read(p);
        return new{Image=image,RawVision=raw,Observation=p,ActiveCategory=MaterialMarketClassifier.ActiveCategory(raw),Plans=plans,Quote=quote,
            RequestedBatches=MaterialMarketTargets.Items.Select(i=>new{Item=i,Request=new MaterialBatchRequest(i.Id,5),Assessment=MaterialPurchaseReader.Assess(new(i.Id,5),quote)}),NativeInput=false};
    }
    public static void Check(string source,string output) {
        Directory.CreateDirectory(output);using var doc=JsonDocument.Parse(File.ReadAllText(source));var rows=doc.RootElement.GetProperty("Rows").EnumerateArray().ToDictionary(r=>r.GetProperty("Image").GetString()!,r=>r.Clone());
        var checks=new List<object>();int failed=0;
        void Assert(string name,bool pass){checks.Add(new{Name=name,Passed=pass});if(!pass)failed++;}
        JsonElement Row(string name)=>rows[name+".png"];
        foreach(var (name,page) in new[]{("M01_home","Home"),("M02_market_empty","MaterialMarket"),("M03_computer_top","MaterialMarket"),("M04_computer_scrolled","MaterialMarket"),("M05_chip_purchase","Purchase"),("M06_military","MaterialMarket"),("M07_radio_purchase","Purchase"),("M08_rope_purchase","Purchase"),("M09_keys_negative","MaterialMarket")})Assert(name+" page",Row(name).GetProperty("Observation").GetProperty("Page").GetString()==page);
        foreach(var (name,id,unit) in new[]{("M05_chip_purchase","chip",32249m),("M07_radio_purchase","radio",31916m),("M08_rope_purchase","rope",40504m)}) {
            var q=Row(name).GetProperty("Quote");
            Assert(name+" identity",q.ValueKind==JsonValueKind.Object&&q.GetProperty("ItemId").GetString()==id);
            foreach(var (field,value) in new[]{("Quantity",1m),("UnitPrice",unit),("Total",unit)})Assert(name+" "+field,q.ValueKind==JsonValueKind.Object&&q.GetProperty(field).ValueKind==JsonValueKind.Number&&q.GetProperty(field).GetDecimal()==value);
            Assert(name+" consistency",q.ValueKind==JsonValueKind.Object&&q.GetProperty("Consistent").GetBoolean());
            Assert(name+" do not reuse default one as batch size",Row(name).GetProperty("RequestedBatches").EnumerateArray().Single(x=>x.GetProperty("Item").GetProperty("Id").GetString()==id).GetProperty("Assessment").GetProperty("Status").GetString()=="NeedsFreshBulkQuote");
        }
        bool HasPlan(string name,string id)=>Row(name).GetProperty("Plans").EnumerateArray().Any(p=>p.GetProperty("Step").GetProperty("Id").GetString()==id&&p.GetProperty("Error").ValueKind==JsonValueKind.Null);
        Assert("market entry",HasPlan("M01_home","materials:market"));
        Assert("empty favorites expands misc root",HasPlan("M02_market_empty","materials:expand-misc"));
        Assert("chip first viewport scrolls rather than clicking clipped card",HasPlan("M03_computer_top","materials:scroll"));
        Assert("chip fully visible after scroll",HasPlan("M04_computer_scrolled","materials:detail:chip"));
        Assert("radio exact military card",HasPlan("M06_military","materials:detail:radio"));
        Assert("rope exact military card",HasPlan("M06_military","materials:detail:rope"));
        Assert("key page supplies no configured material click",!Row("M09_keys_negative").GetProperty("Plans").EnumerateArray().Any(p=>p.GetProperty("Step").GetProperty("Id").GetString()!.StartsWith("materials:detail:")));
        Assert("all browse plans resolve",rows.Values.SelectMany(r=>r.GetProperty("Plans").EnumerateArray()).All(p=>p.GetProperty("Error").ValueKind==JsonValueKind.Null));
        var quote=new MaterialPurchaseQuote("chip","芯片处理器",5,33000,165000,true,"test","test");
        Assert("price thresholds explicitly pending",MaterialPurchaseReader.Assess(new("chip",5),quote).Status=="NeedsPriceLimit");
        Assert("limit compares fresh batch total",MaterialPurchaseReader.Assess(new("chip",5,34000,160000),quote).Status=="OverLimit");
        Assert("accepted quote is not a submitted purchase",MaterialPurchaseReader.Assess(new("chip",5,34000,170000),quote).Status=="QuoteWithinConfiguredLimits");
        Assert("wrong product cannot reuse quote",MaterialPurchaseReader.Assess(new("rope",5,50000),quote).Status=="NeedsVerifiedQuote");
        Assert("user leaves price thresholds for later",MaterialPurchaseReader.RestockDecision(new("chip",5),quote,MaterialStorageState.HasRoomForRequestedBatch)=="WaitForPriceThreshold");
        Assert("warehouse full terminates replenishment",MaterialPurchaseReader.RestockDecision(new("chip",5,34000),quote,MaterialStorageState.Full)=="StopWarehouseFull");
        Assert("unknown capacity never implies space for five",MaterialPurchaseReader.RestockDecision(new("chip",5,34000),quote,MaterialStorageState.Unknown)=="NeedsInventorySpaceCheck");
        Assert("high price stops buying",MaterialPurchaseReader.RestockDecision(new("chip",5,32000),quote,MaterialStorageState.HasRoomForRequestedBatch)=="StopHighPrice");
        Assert("uncertain fill never becomes automatic retry",MaterialPurchaseReader.RestockDecision(new("chip",5,34000),quote,MaterialStorageState.HasRoomForRequestedBatch,true)=="ReconcilePreviousPurchaseBeforeRetry");
        File.WriteAllText(Path.Combine(output,"acceptance.json"),JsonSerializer.Serialize(new{Passed=failed==0,Failed=failed,NativeInput=false,Purchases=0,Checks=checks,Scope="Actual screenshot reads and browse planning plus synthetic five-item price-limit checks; no quantity input or real purchases"},NavigationEvidenceLog.Json));
        if(failed>0)throw new InvalidOperationException($"Material market checks failed: {failed}");
    }
}
