using System.IO;
using System.Text.Json;
using FieldTrader.Navigation;
namespace FieldTrader.Market;

public sealed record RandallQuote(string ItemId,string Name,decimal? Total,decimal? TopUp,decimal? Market,decimal? ReferenceGross,string? PrimaryAction,bool Repeatable,int? CooldownSeconds);
public static class RandallValidation {
    public static async Task Replay(string config,string images,string output) {
        Directory.CreateDirectory(output);await using var pipeline=new PythonVisionPipeline(config);var rows=new List<object>();
        foreach(var file in Directory.GetFiles(images,"*.png").Order()) {
            var s=await new ImageFileCapture(file).CaptureAsync(default);var raw=await pipeline.AnalyzeAsync(s,default);s=s with{Width=raw.Width,Height=raw.Height};
            var row=Inspect(s,raw,Path.GetFileName(file));rows.Add(row);
            File.WriteAllText(Path.Combine(output,Path.GetFileNameWithoutExtension(file)+".json"),JsonSerializer.Serialize(row,NavigationEvidenceLog.Json));Console.WriteLine(Path.GetFileName(file));
        }
        File.WriteAllText(Path.Combine(output,"replay.json"),JsonSerializer.Serialize(new{NativeInput=false,Transactions=0,Rows=rows},NavigationEvidenceLog.Json));
    }
    public static void Reparse(string source,string output) {
        Directory.CreateDirectory(output);using var d=JsonDocument.Parse(File.ReadAllText(source));
        var rows=d.RootElement.GetProperty("Rows").EnumerateArray().Select(r=>Inspect(r.GetProperty("Observation").Deserialize<PageObservation>(NavigationEvidenceLog.Json)!.Screen,r.GetProperty("RawVision").Deserialize<VisionResult>(NavigationEvidenceLog.Json)!,r.GetProperty("Image").GetString()!)).ToArray();
        File.WriteAllText(Path.Combine(output,"replay.json"),JsonSerializer.Serialize(new{NativeInput=false,Transactions=0,Rows=rows},NavigationEvidenceLog.Json));
    }
    static object Inspect(CapturedScreen s,VisionResult raw,string name) {
        var p=new RandallPageClassifier().Classify(s,raw);var plans=new List<object>();var planner=new FieldTrader.Navigation.NavigationPlanner(new());var steps=new List<NavigationStep>();
        foreach(var item in RandallTargets.Items)if(RandallRoutes.Next(p,item) is {} next)steps.Add(next);
        if(p.Page=="Exchange")steps.Add(RandallRoutes.Cancel());
        foreach(var step in steps.DistinctBy(t=>t.Id)) {
            try{plans.Add(new{Step=step,Plan=planner.Locate(p,step,0),Error=(string?)null});}
            catch(NavigationEvidenceException e){plans.Add(new{Step=step,Plan=(PlannedAction?)null,Error=e.Message});}
        }
        RandallQuote? quote=null;
        if(p.Page=="Exchange") {
            var identities=RandallTargets.Items.Where(i=>new VisualTargetResolver().Resolve(p,new(i.Name,"randall_detail",RandallRoutes.All,"道具兑换",RandallRoutes.All),true).Length==1).ToArray();
            if(identities.Length==1) {
                var tokens=p.Vision.Texts.Where(t=>t.Bounds.X>.48&&t.Bounds.Y>.76).ToArray();
                var total=ExchangePanelMoney.Read(tokens,"合计价格");var cash=ExchangePanelMoney.Read(tokens,"补齐价格");var market=ExchangePanelMoney.Read(tokens,"产品市场价");
                var actions=p.Vision.Texts.Where(t=>t.Confidence>=.95&&t.Bounds.X>.65&&t.Bounds.Y>.85&&t.Label is "兑换" or "补齐道具").Select(t=>t.Label).Distinct().ToArray();
                var i=identities[0];quote=new(i.Id,i.Name,total,cash,market,market-total,actions.Length==1?actions[0]:null,i.Repeatable,i.CooldownSeconds);
            }
        }
        return new{Image=name,RawVision=raw,Observation=p,ActiveCategory=RandallPageClassifier.ActiveCategory(raw),Targets=RandallTargets.Items.Select(i=>new{Item=i,Availability=RandallRoutes.Availability(p,i)}),Plans=plans,Quote=quote,NativeInput=false};
    }
    public static void Check(string replay,string output) {
        Directory.CreateDirectory(output);using var doc=JsonDocument.Parse(File.ReadAllText(replay));var rows=doc.RootElement.GetProperty("Rows").EnumerateArray().ToDictionary(r=>r.GetProperty("Image").GetString()!,r=>r.Clone());
        var checks=new List<object>();int failed=0;
        void Assert(string name,bool pass){checks.Add(new{Name=name,Passed=pass});if(!pass)failed++;}
        JsonElement Row(string name)=>rows[name+".png"];
        foreach(var (image,page) in new[]{("R01_profile","ContactProfile"),("R02_masks","ContactShop"),("R03_dk8_owned","Exchange"),("R04_headsets","ContactShop"),("R05_gs2_owned","Exchange"),("R06_dk8_missing","Exchange")})Assert(image+" page",Row(image).GetProperty("Observation").GetProperty("Page").GetString()==page);
        foreach(var (image,total,cash,market,action) in new[]{("R03_dk8_owned",22291m,(decimal?)null,46050m,"兑换"),("R05_gs2_owned",51867m,(decimal?)null,85120m,"兑换"),("R06_dk8_missing",35474m,(decimal?)35474,46050m,"补齐道具")}) {
            var q=Row(image).GetProperty("Quote");
            foreach(var (field,value) in new[]{("Total",(decimal?)total),("TopUp",cash),("Market",market)})Assert(image+" "+field,q.ValueKind==JsonValueKind.Object&&(value is null?q.GetProperty(field).ValueKind==JsonValueKind.Null:q.GetProperty(field).ValueKind==JsonValueKind.Number&&q.GetProperty(field).GetDecimal()==value));
            Assert(image+" actual button",q.ValueKind==JsonValueKind.Object&&q.GetProperty("PrimaryAction").GetString()==action);
        }
        Assert("all navigation plans resolve",rows.Values.SelectMany(r=>r.GetProperty("Plans").EnumerateArray()).All(r=>r.GetProperty("Error").ValueKind==JsonValueKind.Null));
        foreach(var (image,id) in new[]{("R02_masks","dk8"),("R04_headsets","gs2")})Assert(image+" requested item opens",Row(image).GetProperty("Plans").EnumerateArray().Any(p=>p.GetProperty("Step").GetProperty("Id").GetString()=="randall:detail:"+id&&p.GetProperty("Error").ValueKind==JsonValueKind.Null));
        var now=DateTimeOffset.UtcNow;var available=new RandallAvailability(true,false,null,1,1);var dk=RandallTargets.Items[0];var gs=RandallTargets.Items[1];
        Assert("DK8 repeatable without artificial cooldown",RandallRoutes.Eligible(dk,available,now,now));
        Assert("GS2 cannot repeat within five hours",!RandallRoutes.Eligible(gs,available,now,now.AddHours(-4.99)));
        Assert("GS2 becomes eligible at five hours",RandallRoutes.Eligible(gs,available,now,now.AddHours(-5)));
        Assert("screen cooldown overrides elapsed ledger",!RandallRoutes.Eligible(gs,available with{Countdown="00:01:00"},now,now.AddHours(-6)));
        Assert("no unlimited loop implied by repeatable flag",RandallTargets.Items.Length==2&&dk.Repeatable&&gs.CooldownSeconds==18000&&!gs.Repeatable);
        File.WriteAllText(Path.Combine(output,"acceptance.json"),JsonSerializer.Serialize(new{Passed=failed==0,Failed=failed,NativeInput=false,Transactions=0,Checks=checks,Scope="Actual file recognition, navigation plans and synthetic cooldown boundaries; no executed transitions"},NavigationEvidenceLog.Json));
        if(failed>0)throw new InvalidOperationException($"Randall checks failed: {failed}");
    }
}
