using System.Security.Cryptography;
using System.Text;
using FieldTrader.Market;
namespace FieldTrader.Navigation;
// Explicit screenshot-layout adapter. These regions are NOT additional YOLO classes.
public static class NavigationSceneAdapter {
    public static readonly string[] Contacts=["乔尔·加里森","拉里·马里亚姆","迪克·文森","弗拉德伦","托马斯·爱德华","兰德尔·费舍"];
    public static PageObservation Enrich(PageObservation p) {
        VisualTargetResolver.Validate(p);
        var words=p.Vision.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence).ToArray();
        bool Exact(string label)=>words.Any(t=>Geometry.Normalize(t.Label)==Geometry.Normalize(label));
        bool Has(string label)=>words.Any(t=>Geometry.Normalize(t.Label).Contains(Geometry.Normalize(label)));
        var regions=new List<LayoutRegion>();var page=p.Page;var confidence=p.Confidence;var sources=new List<string>(p.Sources??[]);
        bool modal=p.Vision.Regions.Any(t=>t.Confidence>=.6&&t.Label is "trade_panel" or "confirm_dialog" or "result_region");
        if(!modal&&p.Errors.Length==0) {
            var nav=new[]{("仓库",.3125),("联络人",.5),("市场",.59375)};
            bool bar=nav.All(n=>words.Count(t=>Geometry.Normalize(t.Label)==Geometry.Normalize(n.Item1)&&Geometry.Contains(new(n.Item2,.95,.09375,.05),t.Bounds))==1);
            if(bar)foreach(var (name,x) in nav) {
                var token=words.Single(t=>Geometry.Normalize(t.Label)==Geometry.Normalize(name)&&Geometry.Contains(new(x,.95,.09375,.05),t.Bounds));
                regions.Add(new(new("layout_menu",token.Confidence,new(x,.95,.09375,.05)),"LayoutRule:bottom-nav-v1; calibrated b01_01; NOT YOLO",["仓库","联络人","市场"]));
            }
            var contactNames=Contacts.Select((name,index)=>(name,index,found:words.Where(t=>t.Bounds.X>=.24&&t.Bounds.X<.5&&Geometry.Normalize(t.Label)==Geometry.Normalize(name)).ToArray())).Where(c=>c.found.Length==1).ToArray();
            if(contactNames.Length>=3&&Exact("购买")&&Exact("出售")) {
                page="ContactList";confidence=contactNames.Min(c=>c.found[0].Confidence);sources.Add("LayoutRule:contact-list-v1; 3+ independent names and buy/sell anchors");
                foreach(var c in contactNames){
                    var box=new Region(.2,.218+c.index*.0725,.342,.072);
                    if(Geometry.Contains(box,c.found[0].Bounds))regions.Add(new(new("contact_entry",c.found[0].Confidence,box),"LayoutRule:contact-row-v1; calibrated b02_01; NOT YOLO",contactNames.Select(c=>c.name).ToArray()));
                }
                regions.Add(new(new("contact_profile",confidence,new(.56,.218,.245,.5)),"LayoutRule:contact-profile-v1; NOT YOLO",contactNames.Select(c=>c.name).ToArray()));
            }else if(bar&&Has("进入暗区")&&Has("每日任务")&&Has("赛季手册")){page="Home";confidence=.95;sources.Add("LayoutRule:lobby-v1; 3 independent anchors + bottom menu");}
            else if(page=="Unknown"&&bar&&Has("装备")&&Has("仓库容量")){page="Warehouse";confidence=.95;sources.Add("LayoutRule:warehouse explicit title+capacity; no inventory-slot detector");}
            else if(page=="Unknown"&&bar&&p.Vision.Regions.Any(t=>t.Label=="product_card"&&t.Confidence>=.8)&&Has("限购")){page="Contact";confidence=.95;sources.Add("LayoutRule:merchant product cards + limited-quantity anchor");}
            if(page=="Market")foreach(var token in words.Where(t=>t.Bounds.X>=.03&&t.Bounds.X<.18&&t.Bounds.Y>.16&&t.Bounds.Y<.85&&t.Label.Length>=2&&t.Label.Length<=8&&!t.Label.Any(char.IsDigit))) {
                var box=new Region(.03,Math.Max(.16,token.Bounds.Y-.006),.15,Math.Min(.04,.9-token.Bounds.Y));
                if(Geometry.Contains(box,token.Bounds))regions.Add(new(new("category_tab",token.Confidence,box),"LayoutRule:market-left-row-v1; NOT YOLO; bounded column",["购买","我的交易"]));
            }
        }
        var layout=p.Layout+"|"+string.Join("|",regions.Select(r=>$"{r.Token.Label}:{r.Token.Bounds}"));
        return p with{Page=page,Confidence=confidence,Sources=sources.ToArray(),LayoutRegions=regions.ToArray(),Layout=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layout)))};
    }
}
public static class VisualNavigationRoutes {
    public static TargetQuery Menu(string name)=>new(name,"layout_menu",new(0,.94,1,.06),"仓库",new(.31,.94,.11,.06));
    public static NavigationTask Build(PageObservation source,string destination,string? contact=null,CatalogItem? item=null,string? category=null) {
        var steps=new List<NavigationStep>();var page=source.Page;
        NavigationStep Step(string id,string before,TargetQuery target,string after,TargetQuery? result=null)=>new(id,before,target,InputOperation.Click,after,TimeSpan.FromSeconds(20),FailurePolicy.Pause,Effect:LayoutEffect.MayChange,ExpectedResultTarget:result);
        if(page=="Home"&&destination is "Market" or "ContactList" or "Warehouse") {
            string name=destination=="Market"?"市场":destination=="Warehouse"?"仓库":"联络人";
            steps.Add(Step("open:"+destination,page,Menu(name),destination));page=destination;
        }
        if(page=="ContactList"&&!string.IsNullOrWhiteSpace(contact)) {
            steps.Add(Step("contact:"+contact,page,new(contact,"contact_entry",new(.19,.21,.36,.52),NavigationSceneAdapter.Contacts[0],new(.19,.21,.36,.52)),page,
                new(contact,"contact_profile",new(.55,.21,.27,.53),contact,new(.65,.22,.14,.1))));
        }
        if(page=="Market"&&item is not null) {
            if(!string.IsNullOrWhiteSpace(category))steps.Add(Step("category:"+category,page,new(category,"category_tab",new(.02,.16,.18,.74),"我的交易",new(0,0,.4,.1)),page) with{RequiredAfterText=[item.Name]});
            steps.AddRange(NavigationRecipes.OpenMarketProduct(item,new(item.Name,"product_card",new(.19,.15,.79,.77),"我的交易",new(0,0,.4,.1))).Steps);
        }
        if(page=="Contact"&&item is not null)steps.Add(Step("detail:"+item.Id,page,new(item.Name,"product_card",new(.18,.12,.7,.8),"联络人",new(0,.94,1,.06)),"Exchange",new(item.Name,"detail_panel",new(0,0,1,1),"道具兑换",new(.45,0,.45,.2))));
        if(steps.Count==0)throw new NavigationEvidenceException(page=="Unknown"?"UNKNOWN: no supported route":"No calibrated browse route for current page/intent; transaction buttons are excluded");
        return new("visual:"+Guid.NewGuid().ToString("N"),steps.ToArray(),ExecutionMode.RecognizeOnly);
    }
}
