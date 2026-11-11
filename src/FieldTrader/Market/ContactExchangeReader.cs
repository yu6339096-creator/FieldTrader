using System.Text.RegularExpressions;
using FieldTrader.Navigation;
namespace FieldTrader.Market;

public sealed record ExchangeMaterialStock(string Name,int Owned,int Required);
public sealed record ContactExchangeQuote(decimal MaterialTotal,decimal CashRequired,decimal? ProductReference,
    ExchangeMaterialStock[] Materials,string Button,Region ButtonBounds,string RecipeFingerprint);
public sealed record CircuitPage(string Kind,string? Contact,string? Category,VisionResult Vision);
public sealed record ContactProductCard(ContactExchangeTarget Item,Region Bounds,bool Unavailable,int OutputQuantity);

public static class ContactExchangeReader {
    static string N(string s)=>ContactExchangeCatalog.Key(s);
    public static VisualToken[] Words(VisionResult v)=>AutoBuyRules.Words(v);
    public static CircuitPage Page(VisionResult v) {
        AutoBuyRules.Guard(v,true);var words=Words(v);
        bool Has(string s)=>words.Any(t=>N(t.Label)==N(s));
        var headerContacts=NavigationSceneAdapter.Contacts.Where(c=>words.Any(t=>t.Bounds.X<.5&&t.Bounds.Y<.12&&N(t.Label)==N(c))).ToArray();
        string? contact=headerContacts.Length==1?headerContacts[0]:null;
        if(words.Any(t=>t.Label.Contains("已集齐全部兑换道具")))return new("Confirmation",contact,null,v);
        bool detailTitle=words.Any(t=>t.Bounds.X<.5&&t.Bounds.Y<.16&&ContactExchangeCatalog.Items.Any(i=>ContactExchangeCatalog.Matches(t.Label,i.Name)));
        if(Has("道具兑换")&&Has("兑换列表")&&(Has("需要提交以下道具")||detailTitle))return new("Exchange",contact,null,v);
        if(Has("购买数量"))return new("Purchase",null,null,v);
        var contacts=NavigationSceneAdapter.Contacts.Count(c=>words.Any(t=>t.Bounds.X>.19&&t.Bounds.X<.55&&t.Bounds.Y>.2&&N(t.Label)==N(c)));
        if(contacts>=3&&Has("购买")&&Has("出售"))return new("ContactList",null,null,v);
        if(contact is not null&&words.Count(t=>t.Bounds.X<.5&&t.Bounds.Y>.115&&t.Bounds.Y<.16)>=2) {
            var categories=words.Where(t=>t.Bounds.X<.5&&t.Bounds.Y>.115&&t.Bounds.Y<.16).Select(t=>t.Label.TrimStart('①')).Distinct().Where(c=>
                v.PixelMeasurements?.Any(p=>p.Key.EndsWith("TabOrange:"+c,StringComparison.Ordinal)&&p.Value>=.12)==true).ToArray();
            return new("ContactShop",contact,categories.Length==1?categories[0]:null,v);
        }
        if(Has("进入暗区")||Has("每日任务"))return new("Home",null,null,v);
        if(words.Count(t=>t.Bounds.Y>.94&&new[]{"仓库","联络人","市场"}.Contains(t.Label))>=2)return new("Main",contact,null,v);
        return new("Unknown",contact,null,v);
    }
    public static bool MaterialsConsumed(VisionResult before,VisionResult after,AutoBuyRequest request) {
        try {
            var target=ContactExchangeCatalog.Items.Single(t=>t.Id==request.TargetId);
            var a=Quote(before,target);var b=Quote(after,target);
            return a.Materials.Length==b.Materials.Length&&a.Materials.All(old=>old.Owned>=old.Required
                &&b.Materials.Count(now=>N(now.Name)==N(old.Name)&&now.Required==old.Required&&now.Owned==old.Owned-old.Required)==1);
        }catch(InvalidOperationException){return false;}
    }
    public static bool ExplicitSuccess(VisionResult v,AutoBuyRequest request) {
        var page=Page(v);var words=Words(v);var target=ContactExchangeCatalog.Items.Single(i=>i.Id==request.TargetId);
        // Require a success message naming the requested product, not a generic toast on an unrelated page.
        return page.Kind is "Exchange" or "ContactShop"&&page.Contact==target.Contact
            &&words.Any(t=>t.Label.StartsWith("兑换成功：",StringComparison.Ordinal)
                &&ContactExchangeCatalog.Matches(t.Label[5..],request.ItemName));
    }
    public static bool DetailSoldOut(VisionResult v)=>Words(v).Any(t=>t.Label.Contains("已售罄")&&t.Bounds.X>.4&&t.Bounds.X<.65&&t.Bounds.Y>.13&&t.Bounds.Y<.24);
    public static ContactProductCard[] Cards(CircuitPage page) {
        if(page.Kind!="ContactShop")return [];
        var words=Words(page.Vision);var names=words.Where(t=>t.Bounds.X>.04&&t.Bounds.X<.48&&t.Bounds.Y>.16&&t.Bounds.Y<.90
            &&ContactExchangeCatalog.Items.Any(i=>i.Contact==page.Contact&&i.Category==page.Category&&ContactExchangeCatalog.Matches(t.Label,i.Name))).ToArray();
        var result=new List<ContactProductCard>();
        foreach(var name in names) {
            var matches=ContactExchangeCatalog.Items.Where(i=>i.Contact==page.Contact&&i.Category==page.Category&&ContactExchangeCatalog.Matches(name.Label,i.Name)).ToArray();
            if(matches.Length!=1||names.Count(t=>ContactExchangeCatalog.Matches(t.Label,matches[0].Name))!=1)continue;
            var boxes=page.Vision.Regions.Where(r=>r.Label=="product_card"&&r.Confidence>=.6&&Geometry.Contains(r.Bounds,name.Bounds)&&r.Bounds.X<.5).OrderBy(r=>r.Bounds.Width*r.Bounds.Height).ToArray();
            // OCR card headings define the four-column grid when the auxiliary detector misses it.
            double col=.0475+Math.Clamp((int)((name.Bounds.X-.0475)/.1075),0,3)*.1075;
            var box=boxes.Length>0?boxes[0].Bounds:new Region(col,name.Bounds.Y-.004,.105,Math.Min(.235,.91-name.Bounds.Y));
            var inside=words.Where(t=>Geometry.Contains(box,t.Bounds)).ToArray();
            bool unavailable=inside.Any(t=>t.Label.Contains("已售罄")||Regex.IsMatch(N(t.Label),@"\d{1,2}:\d{2}:\d{2}")||Regex.IsMatch(N(t.Label),@"^限购:?0/\d+$"));
            var quantities=inside.Select(t=>Regex.Match(N(t.Label),@"^[x×]([1-9]\d{0,4})$")).Where(m=>m.Success).Select(m=>int.Parse(m.Groups[1].Value)).Distinct().ToArray();
            bool needsCount=matches[0].Category==ContactExchangeCatalog.Limited&&matches[0].ItemClass?.StartsWith("ammo_")==true||matches[0].ItemClass?.StartsWith("grenade_")==true;
            result.Add(new(matches[0],box,unavailable,quantities.Length==1?quantities[0]:needsCount?0:1));
        }
        return result.ToArray();
    }
    public static ContactExchangeQuote Quote(VisionResult v,ContactExchangeTarget target) {
        var page=Page(v);var words=Words(v);
        if(page.Kind!="Exchange"||page.Contact is {} c&&c!=target.Contact
            ||words.Count(t=>t.Bounds.X<.5&&t.Bounds.Y<.16&&ContactExchangeCatalog.Matches(t.Label,target.Name))!=1)
            throw new InvalidOperationException("兑换详情商品或联络人与当前路线不符");
        var headers=words.Where(t=>N(t.Label)=="需要提交以下道具").ToArray();var lists=words.Where(t=>N(t.Label)=="兑换列表").ToArray();
        if(headers.Length!=1||lists.Length!=1)throw new InvalidOperationException("兑换材料区域不唯一");
        var band=words.Where(t=>t.Bounds.X>=headers[0].Bounds.X-.012&&t.Bounds.Y>headers[0].Bounds.Y+headers[0].Bounds.Height&&t.Bounds.Y<lists[0].Bounds.Y).ToArray();
        var counts=band.Where(t=>Regex.IsMatch(N(t.Label),@"^\d{1,5}/[1-9]\d{0,4}$")).ToArray();
        var stocks=new List<ExchangeMaterialStock>();
        foreach(var count in counts) {
            var names=band.Where(t=>t.Bounds.Y<count.Bounds.Y&&count.Bounds.Y-t.Bounds.Y<.12&&t.Bounds.X<=count.Bounds.X+.005
                &&count.Bounds.X-t.Bounds.X<.075&&Regex.IsMatch(N(t.Label),@"^[\p{L}\d]+$")&&!Regex.IsMatch(N(t.Label),@"^\d+$")).OrderByDescending(t=>t.Bounds.X).ThenByDescending(t=>t.Bounds.Y).ToArray();
            if(names.Length==0)throw new InvalidOperationException("材料数量缺少对应名称");
            var nums=N(count.Label).Split('/');stocks.Add(new(ContactExchangeCatalog.Material(names[0].Label),int.Parse(nums[0]),int.Parse(nums[1])));
        }
        if(stocks.Count==0||stocks.Select(m=>N(m.Name)).Distinct().Count()!=stocks.Count)throw new InvalidOperationException("材料名称或库存数量缺失/重复");
        if(target.Recipe is {} expected&&(expected.Count!=stocks.Count||stocks.Any(m=>!expected.Any(p=>N(p.Key)==N(m.Name)&&p.Value==m.Required))))
            throw new InvalidOperationException("实时配方与截图登记的配方不符");
        var footer=words.Where(t=>t.Bounds.X>.45&&t.Bounds.Y>.7).ToArray();
        var total=ExchangePanelMoney.Read(footer,"合计价格");var topup=ExchangePanelMoney.Read(footer,"补齐价格");var market=ExchangePanelMoney.Read(footer,"产品市场价");
        bool owned=stocks.All(m=>m.Owned>=m.Required);string button=owned?"兑换":"补齐道具";
        if(total is not >0||!owned&&topup is not >0||owned&&topup is >0||topup>total)throw new InvalidOperationException("合计价值、库存和本次补齐报价未核对一致");
        var buttons=words.Where(t=>t.Bounds.X>.5&&t.Bounds.Y>.7&&N(t.Label)==button).ToArray();
        if(buttons.Length!=1)throw new InvalidOperationException("兑换/补齐按钮未唯一识别");
        return new(total.Value,owned?0:topup!.Value,market,stocks.ToArray(),button,buttons[0].Bounds,
            string.Join(";",stocks.OrderBy(m=>N(m.Name)).Select(m=>$"{N(m.Name)}:{m.Owned}/{m.Required}")));
    }
    public static decimal? EstimatedNet(ContactExchangeQuote q,ContactCircuitSettings settings) {
        if(!settings.FeesVerified||q.ProductReference is not >0)return null;
        var fees=new FeePolicy(settings.SaleRate,settings.ManagementRate,0,true,"UserCircuitSettings");
        if(!fees.Valid)return null;
        return q.ProductReference-fees.SaleFee(q.ProductReference.Value)-fees.ListingFee(q.ProductReference.Value,1)-q.MaterialTotal;
    }
}
