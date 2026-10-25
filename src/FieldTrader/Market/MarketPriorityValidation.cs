using System.Text.Json;
using System.IO;
namespace FieldTrader.Market;
public static class MarketPriorityValidation {
    public static void Reparse(string directory,string output) {
        Directory.CreateDirectory(output);var db=new MarketDatabase(Path.Combine(output,"market.sqlite"));db.ImportSeed(MarketDatabase.BundledSeedPath);
        var analysis=new TradingAnalysis(db,null);var rows=new List<object>();
        foreach(var file in Directory.GetFiles(Path.Combine(directory,"evidence"),"*.json").OrderBy(f=>{
            using var j=JsonDocument.Parse(File.ReadAllText(f));return Path.GetFileName(j.RootElement.GetProperty("Screen").GetProperty("Frame").GetProperty("ScreenshotPath").GetString());
        })) {
            using var j=JsonDocument.Parse(File.ReadAllText(file));var v=j.RootElement.GetProperty("Result").GetProperty("Vision").Deserialize<VisionResult>()!;
            var p=MarketPerception.Fuse(v,db.Catalog());var facts=TradeScreenReader.Read(p,db.Catalog());analysis.Observe(p,true,DateTimeOffset.UtcNow);
            rows.Add(new{Image=Path.GetFileName(j.RootElement.GetProperty("Screen").GetProperty("Frame").GetProperty("ScreenshotPath").GetString()),Page=p.Page.ToString(),p.Quotes,TradeFacts=facts,analysis.Reference});
        }
        File.WriteAllText(Path.Combine(output,"screenshots.json"),JsonSerializer.Serialize(new{Mode="ReparseActualWorkerOutput",NativeInput=false,Results=rows},TradingAnalysis.JsonOptions));
    }
    public static void Run(string output) {
        Directory.CreateDirectory(output);var checks=new List<string>();
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);checks.Add(label);}
        var now=DateTimeOffset.UtcNow;
        var catalog=new[]{new CatalogItem("m","材料",Confirmed:true),new CatalogItem("p","成品",Confirmed:true)};
        PerceptionResult Frame(string name,string context,decimal price,int? quantity,bool replay=false)=>new(new(Guid.NewGuid().ToString("N"),now,"hash",1000,1000,[],[],0,0,0),context=="purchase-unit"?MarketPage.Purchase:MarketPage.Sell,false,"",[new(name,price,quantity,1,"test",context,true)],[]);
        var db=new MarketDatabase(Path.Combine(output,Guid.NewGuid()+".sqlite"));foreach(var item in catalog)db.UpsertItem(item);
        var recipe=new ExchangeRecipe("isolation","fixture","p",1,[new("m",1)],0,VerificationStatus.Verified,"fixture",1);db.SaveRecipe(recipe);
        for(int i=0;i<3;i++)db.Store(Frame("成品","market-card",1000,1) with{Vision=new("sale"+i,now.AddMinutes(-i*6),"history"+i,1000,1000,[],[],0,0,0)},false);
        var settings=new TradingSettings(new(.08m,0,0,true,"fixture"),new(10000,0,0,0),[],[]);
        var a=new TradingAnalysis(db,settings);
        a.Observe(Frame("材料","purchase-unit",100,5),false,now);
        a.Observe(Frame("成品","sell-displayed",1000,1),false,now);
        var before=a.LastDecisions.Single();
        a.Observe(Frame("材料","sell-displayed",900,5),false,now);
        Check(a.LastDecisions.Single().PurchaseCost==before.PurchaseCost&&before.PurchaseCost==100,"先买后卖同材料不覆盖采购成本");
        a.Observe(Frame("材料","purchase-unit",777,5),true,now);
        Check(a.LastDecisions.All(d=>d.Kind==DecisionKind.SKIP),"切换回放清空实时上下文");
        VisualToken T(string label,double y)=>new(label,.99,new(.65,y,.1,.02));
        PerceptionResult Sale(decimal? fee,decimal? net,decimal? management,int? quantity=5) {
            var words=new List<VisualToken>();
            void Row(string label,decimal? value,double y){if(value is {} n){words.Add(new(label,.99,new(.3,y,.25,.02)));words.Add(T(n.ToString(System.Globalization.CultureInfo.InvariantCulture),y));}}
            Row("上架金额",211045,.3);Row("手续费",fee,.4);Row("成交后预计获得",net,.5);Row("管理费",management,.6);
            return Frame("成品","sell-displayed",42209,quantity) with{Vision=new("sale",now,"hash",1000,1000,[new("trade_panel",.99,new(.2,.2,.6,.6))],words.ToArray(),0,0,0)};
        }
        var refs=new ReferenceAnalysis();refs.Observe(Frame("成品","purchase-unit",10000,1),catalog,false,now);
        var exact=refs.Observe(Sale(14895,196150,6383),catalog,false,now)!;
        Check(exact.Spread==139767,"OCR到账优先，管理费独立一次，无重复税");
        Check(refs.Observe(Sale(14895,null,6383),catalog,false,now)!.Spread==139767,"无到账时采用实际手续费");
        var estimated=refs.Observe(Sale(null,null,null),catalog,false,now)!;
        Check(estimated.Net is null&&estimated.Spread is null&&estimated.Missing.Length>0,"缺少费用不套用8%且不计算净利润");
        Check(refs.Observe(Sale(null,null,null,null),catalog,false,now)!.Cost is null,"数量未知不默认1");
        var events=ExchangeScreenReader.Events([T("已兑换SH12头盔",.1),T("物品价格发生变化，部分购买失败",.15)]);
        Check(events.Contains("exchange_success_candidate")&&events.Contains("partial_failure"),"提示不依赖YOLO通知框，成功与失败并存仍是候选");
        foreach(var (gross,fee,management,net,expected) in new[]{(205820m,13281m,5692m,192539m,186847m),(255199m,22329m,9569m,232870m,223301m),(321283m,20661m,8855m,300622m,291767m)}) {
            var words=new[]{new VisualToken("上架金额",.99,new(.3,.3,.2,.02)),T(gross.ToString(),.3),new("手续费",.99,new(.3,.4,.2,.02)),T(fee.ToString(),.4),new("管理费",.99,new(.3,.5,.2,.02)),T(management.ToString(),.5),new("成交后预计获得",.99,new(.3,.6,.2,.02)),T(net.ToString(),.6)};
            var input=Frame("成品","sell-displayed",gross,1) with{Vision=new("fees",now,"hash",1000,1000,[new("trade_panel",.99,new(.2,.2,.6,.6))],words,0,0,0)};
            var terms=TradeScreenReader.Read(input,catalog).Sale!;
            Check(terms.NetAfterAllFees==expected&&fee!=gross*.08m,$"截图费用夹具 {gross}：不套用固定8%，净回款 {expected}");
        }
        VisualToken Footer(string value,double x,double width)=>new(value,.99,new(x,.8,width,.02));
        var split=new[]{Footer("合计价格：",.50,.05),Footer("323738",.57,.05),Footer("|补齐价格：",.63,.06),Footer("128808",.72,.05)};
        Check(ExchangePanelMoney.Read(split,"合计价格")==323738&&ExchangePanelMoney.Read(split,"补齐价格")==128808,"兑换总价不跨越带竖线的补齐字段");
        var merged=new[]{Footer("合计价格：",.50,.05),Footer("189644丨补齐价格：",.57,.12),Footer("156816",.72,.05)};
        Check(ExchangePanelMoney.Read(merged,"合计价格")==189644&&ExchangePanelMoney.Read(merged,"补齐价格")==156816,"数字和下一字段合并时仍保留两个独立金额");
        var noSeparator=new[]{Footer("合计价格：",.50,.05),Footer("125738补齐价格：",.57,.12),Footer("125738",.72,.05)};
        Check(ExchangePanelMoney.Read(noSeparator,"合计价格")==125738&&ExchangePanelMoney.Read(noSeparator,"补齐价格")==125738,"缺少竖线的926旧图合并字段仍正确关联总价");
        var duplicates=new[]{Footer("产品市场价：①",.50,.07),Footer("产品市场价：",.501,.069),Footer("415,044",.61,.05),Footer("415044",.611,.049)};
        Check(ExchangePanelMoney.Read(duplicates,"产品市场价")==415044,"同一市场价重复OCR一致时去重且忽略信息图标");
        Check(ExchangePanelMoney.Read(duplicates.Append(Footer("415045",.61,.05)),"产品市场价") is null,"重复OCR金额冲突时保持未知");
        File.WriteAllText(Path.Combine(output,"priority-tests.json"),JsonSerializer.Serialize(new{Checks=checks,Passed=true,NativeInput=false},TradingAnalysis.JsonOptions));
    }
}
