using System.IO;
using System.Text.Json;
using FieldTrader.Navigation;
namespace FieldTrader.Market;

// File-only route replay. Transaction and refresh buttons never enter its input queue.
public static class DickValidation {
    public static void Check(string replay,string output) {
        Directory.CreateDirectory(output);
        using var doc=JsonDocument.Parse(File.ReadAllText(replay));
        var rows=doc.RootElement.GetProperty("Rows").EnumerateArray().ToDictionary(r=>r.GetProperty("Image").GetString()!,r=>r.Clone());
        var checks=new List<object>();int failed=0;
        void Assert(string name,bool pass){checks.Add(new{Name=name,Passed=pass});if(!pass)failed++;}
        JsonElement Row(string name)=>rows[name+".png"];
        PageObservation Page(string name)=>Row(name).GetProperty("Observation").Deserialize<PageObservation>(NavigationEvidenceLog.Json)!;
        foreach(var (image,page) in new[]{("D01_home","Home"),("D02_profile","ContactProfile"),("D03_uninitialized","ContactShop"),("D04_limited_cooling","ContactShop"),("D05_gho_detail","Exchange"),("D06_limited_exhausted","ContactShop"),("D07_helmets","ContactShop"),("D08_helmets_scrolled","ContactShop"),("D09_rigs","ContactShop"),("D10_o3_detail","Exchange"),("D11_m4_detail","Exchange"),("D12_a8_detail","Exchange"),("D13_a9_soldout","Exchange"),("D14_926_detail","Exchange")})Assert(image+" page",Page(image).Page==page);
        Assert("initial acquisition is separate state",DickScreenReader.Limited(Page("D03_uninitialized")).State==DickRefreshState.NotAcquired);
        var cooling=DickScreenReader.Limited(Page("D04_limited_cooling"));
        Assert("59:59 means 3599 seconds until next refresh",cooling.State==DickRefreshState.Cooling&&cooling.WaitSeconds==3599&&cooling.Remaining is null);
        Assert("exhausted is zero refreshes, existing goods remain visible",DickScreenReader.Limited(Page("D06_limited_exhausted")) is {State:DickRefreshState.Exhausted,Remaining:0,HasGoods:true});
        foreach(var (image,total,cash,market) in new[]{("D05_gho_detail",49323m,49323m,120310m),("D10_o3_detail",323738m,128808m,383026m),("D11_m4_detail",290948m,290948m,415044m),("D12_a8_detail",192829m,192829m,250640m),("D13_a9_soldout",189644m,156816m,265167m),("D14_926_detail",125738m,125738m,149400m)}) {
            var q=Row(image).GetProperty("Quote");
            foreach(var (field,value) in new[]{("Total",total),("TopUp",cash),("Market",market)})Assert(image+" "+field,q.GetProperty(field).ValueKind==JsonValueKind.Number&&q.GetProperty(field).GetDecimal()==value);
        }
        Assert("all proposed navigation plans resolve",rows.Values.SelectMany(r=>r.GetProperty("Plans").EnumerateArray()).All(r=>r.GetProperty("Error").ValueKind==JsonValueKind.Null));
        var cards=DickScreenReader.Cards(Page("D04_limited_cooling"));var gho=cards.SingleOrDefault(c=>c.Item.Id=="gho2");
        Assert("trained GHO joins model, UI card, OCR and x5",gho is {ItemModelMatched:true,OutputQuantity:5});
        Assert("untrained R37.F ammo is not selected",cards.All(c=>c.Item.Id=="gho2"));
        DickQuote? bound=null;string? bindingError=null;
        if(gho is not null)try{bound=DickQuoteReader.Read(DickQuoteReader.Begin("supplied-session",Page("D04_limited_cooling"),gho),Page("D05_gho_detail"));}catch(NavigationEvidenceException e){bindingError=e.Message;}
        Assert("same workflow binds 5 grenades to total and supplement quote",bound is{OutputQuantity:5,Total:49323,TopUp:49323,Market:120310,Eligibility:"LimitedTrainedCandidate"});
        int rejected=0;
        if(gho is not null) {
            var context=DickQuoteReader.Begin("supplied-session",Page("D04_limited_cooling"),gho);
            foreach(var wrong in new[]{Page("D10_o3_detail"),Page("D05_gho_detail") with{Screen=Page("D05_gho_detail").Screen with{Live=true}}})
                try{DickQuoteReader.Read(context,wrong);}catch(NavigationEvidenceException){rejected++;}
        }
        Assert("wrong product or file/live crossover cannot inherit quantity",rejected==2);
        File.WriteAllText(Path.Combine(output,"acceptance.json"),JsonSerializer.Serialize(new{Passed=failed==0,Failed=failed,NativeInput=false,Refreshes=0,Transactions=0,Checks=checks,BoundQuote=bound,BindingError=bindingError,Scope="Actual screenshot recognition and navigation plan resolution, not executed transitions or trades"},NavigationEvidenceLog.Json));
        if(failed>0)throw new InvalidOperationException($"Dick evidence checks failed: {failed}");
    }
    public static async Task Replay(string config,string images,string output) {
        Directory.CreateDirectory(output);
        await using var pipeline=new PythonVisionPipeline(config);
        var rows=new List<object>();
        foreach(var file in Directory.GetFiles(images,"*.png").Order()) {
            var screen=await new ImageFileCapture(file).CaptureAsync(default);
            var raw=await pipeline.AnalyzeAsync(screen,default);
            screen=screen with{Width=raw.Width,Height=raw.Height};
            var row=Inspect(screen,raw,Path.GetFileName(file));rows.Add(row);
            File.WriteAllText(Path.Combine(output,Path.GetFileNameWithoutExtension(file)+".json"),JsonSerializer.Serialize(row,NavigationEvidenceLog.Json));
            Console.WriteLine(Path.GetFileName(file));
        }
        File.WriteAllText(Path.Combine(output,"replay.json"),JsonSerializer.Serialize(new{Mode="FileOnlyRouteReplay",NativeInput=false,Refreshes=0,Transactions=0,Rows=rows},NavigationEvidenceLog.Json));
    }
    public static void Reparse(string replay,string output) {
        Directory.CreateDirectory(output);
        using var doc=JsonDocument.Parse(File.ReadAllText(replay));var rows=new List<object>();
        foreach(var r in doc.RootElement.GetProperty("Rows").EnumerateArray()) {
            var old=r.GetProperty("Observation").Deserialize<PageObservation>(NavigationEvidenceLog.Json)!;
            rows.Add(Inspect(old.Screen,r.GetProperty("RawVision").Deserialize<VisionResult>(NavigationEvidenceLog.Json)!,r.GetProperty("Image").GetString()!));
        }
        File.WriteAllText(Path.Combine(output,"replay.json"),JsonSerializer.Serialize(new{Mode="ReparseActualSavedInference",NativeInput=false,Refreshes=0,Transactions=0,Rows=rows},NavigationEvidenceLog.Json));
    }
    static object Inspect(CapturedScreen screen,VisionResult raw,string name) {
        var page=new DickPageClassifier().Classify(screen,raw);var plans=new List<object>();
        var cards=DickScreenReader.Cards(page);var active=DickPageClassifier.ActiveCategory(raw);
        var planner=new FieldTrader.Navigation.NavigationPlanner(new());
        var candidates=new List<NavigationStep>();
        foreach(var item in DickTargets.Items.Take(5).Concat(cards.Select(c=>c.Item)).DistinctBy(i=>i.Id)) {
            var step=DickRoutes.Next(page,item,active=="限时物品"?active:item.Category);
            if(step is not null)candidates.Add(step);
        }
        if(page.Page=="Exchange")candidates.Add(DickRoutes.Cancel());
        if(page.Page=="ContactShop"&&DickScreenReader.Limited(page).State!=DickRefreshState.NotAcquired)candidates.Add(DickRoutes.Scroll());
        foreach(var step in candidates.DistinctBy(s=>s.Id)) {
            try{plans.Add(new{Step=step,Plan=planner.Locate(page,step,0),Error=(string?)null});}
            catch(NavigationEvidenceException e){plans.Add(new{Step=step,Plan=(PlannedAction?)null,Error=e.Message});}
        }
        object? quote=null;
        if(page.Page=="Exchange") {
            var identities=DickTargets.Items.Where(i=>page.Vision.Texts.Any(t=>t.Confidence>=.95&&t.Bounds.X<.46&&t.Bounds.Y<.16&&Geometry.Normalize(t.Label)==Geometry.Normalize(i.Name))).ToArray();
            if(identities.Length==1) {
                var tokens=page.Vision.Texts.Where(t=>t.Bounds.X>.48&&t.Bounds.Y>.76).ToArray();
                var total=ExchangePanelMoney.Read(tokens,"合计价格");var cash=ExchangePanelMoney.Read(tokens,"补齐价格");var market=ExchangePanelMoney.Read(tokens,"产品市场价");
                var buttons=LarryPageClassifier.Unique(page.Vision.Texts.Where(t=>t.Confidence>=.95&&t.Bounds.X>.65&&t.Bounds.Y>.85&&t.Label is "兑换" or "补齐道具")).Select(t=>t.Label).Distinct().ToArray();
                quote=new{Item=identities[0],Total=total,TopUp=cash,Market=market,ReferenceGross=market-total,PrimaryAction=buttons.Length==1?buttons[0]:null,
                    OutputQuantity=(int?)null,QuantitySource="Must carry unique originating card from same workflow; detail artwork count is not output quantity",NativeInput=false};
            }
        }
        return new{Image=name,RawVision=raw,Observation=page,ActiveCategory=active,Limited=DickScreenReader.Limited(page),Cards=cards,Plans=plans,Quote=quote,NativeInput=false};
    }
}
