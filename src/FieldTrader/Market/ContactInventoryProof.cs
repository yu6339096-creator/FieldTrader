using System.Text.RegularExpressions;
using FieldTrader.Navigation;
namespace FieldTrader.Market;

public static class ContactInventoryProof {
    static int? ModelAmount(VisionResult v,AutoBuyRequest request) {
        var target=ContactExchangeCatalog.Items.SingleOrDefault(t=>t.Id==request.TargetId);
        if(target?.ItemClass is not {} cls||v.ItemModel is null)return null;
        var art=(v.Items??[]).Where(t=>t.Label==cls&&t.Confidence>=OcrPolicy.MinimumConfidence&&t.Bounds.X>.58).ToArray();
        if(!cls.StartsWith("ammo_"))return art.Length;
        int total=0;var used=new HashSet<Region>();
        foreach(var item in art) {
            var quantities=AutoBuyRules.Words(v).Where(t=>t.Bounds.X>=item.Bounds.X-.004&&t.Bounds.X<=item.Bounds.X+item.Bounds.Width+.015
                &&t.Bounds.Y>=item.Bounds.Y+item.Bounds.Height*.4&&t.Bounds.Y<item.Bounds.Y+item.Bounds.Height+.025&&Regex.IsMatch(Geometry.Normalize(t.Label),@"^[1-9]\d{0,4}$")).ToArray();
            if(quantities.Length!=1||!used.Add(quantities[0].Bounds))return null;
            total+=int.Parse(Geometry.Normalize(quantities[0].Label));
        }
        return total;
    }
    static bool SameModel(VisionResult a,VisionResult b)=>a.ItemModel is not null&&a.ItemModel.Sha256==b.ItemModel?.Sha256;
    public static bool Increased(VisionResult a,VisionResult b,AutoBuyRequest request) {
        if(AutoBuyRules.VisibleProductCount(b,request.ItemName)==AutoBuyRules.VisibleProductCount(a,request.ItemName)+request.OutputQuantity)return true;
        return SameModel(a,b)&&ModelAmount(a,request) is {} before&&ModelAmount(b,request)==before+request.OutputQuantity;
    }
    public static bool Stable(VisionResult a,VisionResult b,AutoBuyRequest request) {
        if(AutoBuyRules.VisibleProductCount(a,request.ItemName)>0&&AutoBuyRules.VisibleProductCount(a,request.ItemName)==AutoBuyRules.VisibleProductCount(b,request.ItemName))return true;
        return SameModel(a,b)&&ModelAmount(a,request) is >0 and var amount&&ModelAmount(b,request)==amount;
    }
}
