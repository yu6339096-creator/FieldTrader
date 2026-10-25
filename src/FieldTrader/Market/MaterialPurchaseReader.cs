using FieldTrader.Navigation;
namespace FieldTrader.Market;

public sealed record MaterialPurchaseQuote(string ItemId,string Name,int? Quantity,decimal? UnitPrice,decimal? Total,bool Consistent,string FrameId,string Hash);
public sealed record MaterialBatchRequest(string ItemId,int? Quantity,decimal? MaximumUnitPrice=null,decimal? MaximumTotal=null);
public sealed record MaterialBatchAssessment(string Status,decimal? EstimateOnly,string Reason);
public enum MaterialStorageState { Unknown, HasRoomForRequestedBatch, Full }
public static class MaterialPurchaseReader {
    public static MaterialPurchaseQuote? Read(PageObservation p) {
        VisualTargetResolver.Validate(p);
        if(p.Page!="Purchase"||p.Errors.Length>0)return null;
        var matches=MaterialMarketTargets.Items.Where(i=>new VisualTargetResolver().Resolve(p,new(i.Name,"materials_detail",MaterialMarketRoutes.All,"购买数量",MaterialMarketRoutes.All),true).Length==1).ToArray();
        var panels=p.Vision.Regions.Where(t=>t.Label=="trade_panel"&&t.Confidence>=.8).ToArray();
        if(matches.Length!=1||panels.Length!=1)return null;
        var words=p.Vision.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Contains(panels[0].Bounds,t.Bounds)).ToArray();
        int? quantity=MaterialMarketQuantity.Read(p.Vision);
        var unit=TradeScreenReader.RowNumber(words,"平均单价");var total=TradeScreenReader.RowNumber(words,"合计");var i=matches[0];
        return new(i.Id,i.Name,quantity,unit,total,quantity>0&&unit>0&&total==unit*quantity,p.Screen.Frame.Id,p.Screen.ContentHash);
    }
    public static MaterialBatchAssessment Assess(MaterialBatchRequest request,MaterialPurchaseQuote? quote) {
        if(!MaterialMarketTargets.Items.Any(i=>i.Id==request.ItemId))throw new ArgumentException("Unknown material");
        if(request.Quantity is null)return new("NeedsRequestedQuantity",null,"No batch quantity supplied; do not copy screenshot's default one");
        if(request.Quantity<=0||request.MaximumUnitPrice<=0||request.MaximumTotal<=0)throw new ArgumentException("Quantity and configured price limits must be positive");
        if(quote is null||quote.ItemId!=request.ItemId||!quote.Consistent)return new("NeedsVerifiedQuote",null,"Fresh same-product quantity and amounts required");
        var estimate=quote.UnitPrice*request.Quantity;
        if(quote.Quantity!=request.Quantity)return new("NeedsFreshBulkQuote",estimate,"Estimate only; update quantity then reread average unit price and total across price tiers");
        if(request.MaximumUnitPrice is null&&request.MaximumTotal is null)return new("NeedsPriceLimit",estimate,"No configured unit-price or total-spend limit");
        if(request.MaximumUnitPrice is {} unit&&quote.UnitPrice>unit||request.MaximumTotal is {} total&&quote.Total>total)return new("OverLimit",estimate,"Current quote exceeds configured spend limit");
        return new("QuoteWithinConfiguredLimits",estimate,"Quote checks only; no purchase submitted and no fill claimed");
    }
    public static string RestockDecision(MaterialBatchRequest request,MaterialPurchaseQuote? quote,MaterialStorageState storage,bool previousPurchaseUncertain=false) {
        if(storage==MaterialStorageState.Full)return "StopWarehouseFull";
        if(previousPurchaseUncertain)return "ReconcilePreviousPurchaseBeforeRetry";
        // Thresholds are deliberately unset until the user configures them.
        if(request.MaximumUnitPrice is null&&request.MaximumTotal is null)return "WaitForPriceThreshold";
        var assessment=Assess(request,quote);
        if(assessment.Status=="OverLimit")return "StopHighPrice";
        if(assessment.Status!="QuoteWithinConfiguredLimits")return assessment.Status;
        if(storage!=MaterialStorageState.HasRoomForRequestedBatch)return "NeedsInventorySpaceCheck";
        return "BatchQuoteReady;SubmissionNotConnected";
    }
}
