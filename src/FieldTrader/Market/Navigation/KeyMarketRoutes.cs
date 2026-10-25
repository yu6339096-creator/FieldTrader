using FieldTrader.Market;
namespace FieldTrader.Navigation;
public sealed record KeyMarketItem(string Id,string Name,string Category);
public static class KeyMarketTargets {
 public static readonly string[] Categories=["前线要塞钥匙","电视台钥匙","机场钥匙"];
 public static readonly KeyMarketItem[] Items=[
  new("F02","二号水闸房钥匙","前线要塞钥匙"),
  new("F03","私人休息室钥匙","前线要塞钥匙"),
  new("F04","枪械储藏室门禁卡","前线要塞钥匙"),
  new("F05","二层休息室钥匙","前线要塞钥匙"),
  new("F06","作战指挥室入口门禁卡","前线要塞钥匙"),
  new("F07","作战指挥室出口门禁卡","前线要塞钥匙"),
  new("T02","二层综合办公室钥匙","电视台钥匙"),
  new("T03","底片室钥匙","电视台钥匙"),
  new("T04","医务室钥匙","电视台钥匙"),
  new("T05","二层台长办公室门禁卡","电视台钥匙"),
  new("T06","后期制作室钥匙","电视台钥匙"),
  new("T07","剪辑室钥匙","电视台钥匙"),
  new("T08","企划室钥匙","电视台钥匙"),
  new("A02","员工休息室钥匙","机场钥匙"),
  new("A03","油库材料堆放间钥匙","机场钥匙"),
  new("A04","户外展会杂物间钥匙","机场钥匙"),
  new("A05","货物质检区钥匙","机场钥匙"),
  new("A06","主管休息室钥匙","机场钥匙"),
  new("A07","控制区权限卡","机场钥匙"),
  new("A08","售票大厅会议室钥匙","机场钥匙"),
  new("A09","高级会客区门禁卡","机场钥匙"),
  new("A10","AOC机密档案室门禁卡","机场钥匙"),
 ];
}
public sealed class KeyMarketClassifier : IPageClassifier {
 private readonly MaterialMarketClassifier inner=new("钥匙",KeyMarketTargets.Categories,KeyMarketTargets.Items.Select(i=>i.Name).ToArray(),"KeyMarket","KeyCategoryOrange:");
 public static string? ActiveCategory(VisionResult v)=>MaterialMarketClassifier.PickCategory(v,KeyMarketTargets.Categories,"KeyCategoryOrange:");
 public PageObservation Classify(CapturedScreen s,VisionResult v)=>inner.Classify(s,v);
}
public static class KeyMarketRoutes {
 static NavigationStep Step(string id,string page,TargetQuery target,string after,TargetQuery? result=null)=>new("keys:"+id,page,target,InputOperation.Click,after,TimeSpan.FromSeconds(40),ExpectedResultTarget:result);
 public static NavigationStep? Next(PageObservation p,KeyMarketItem item,bool enabled,bool selected,int requestedQuantity) {
  if(!KeyMarketTargets.Items.Contains(item))throw new ArgumentException("Unsupported key or category");
  if(!enabled||!selected||requestedQuantity<=0||p.Errors.Length>0)return null;
  if(p.Page=="Home")return Step("market","Home",VisualNavigationRoutes.Menu("市场"),"KeyMarket");
  if(p.Page=="Purchase")return MaterialMarketRoutes.Quantity(p,new(item.Id,item.Name,item.Category,""),requestedQuantity);
  if(p.Page!="KeyMarket")return null;
  if(!p.Vision.Texts.Any(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&Geometry.Contains(MaterialMarketRoutes.Left,t.Bounds)&&t.Label==item.Category))return Step("expand-keys","KeyMarket",new("钥匙","materials_root",MaterialMarketRoutes.Left,"我的交易",MaterialMarketRoutes.Top),"KeyMarket",MaterialMarketRoutes.Category(item.Category));
  if(KeyMarketClassifier.ActiveCategory(p.Vision)!=item.Category)return Step("category:"+item.Category,"KeyMarket",MaterialMarketRoutes.Category(item.Category),"KeyMarket") with{RequiredActiveCategory=item.Category};
  if(!MaterialMarketRoutes.FullyVisible(p,new(item.Id,item.Name,item.Category,"")))return Step("scroll","KeyMarket",new(item.Category,"materials_goods_viewport",MaterialMarketRoutes.Goods,item.Category,new(.195,.10,.25,.055)),"KeyMarket") with{Operation=InputOperation.Scroll,ScrollDelta=-360};
  return Step("detail:"+item.Id,"KeyMarket",new(item.Name,"product_card",MaterialMarketRoutes.Goods,item.Category,new(.195,.10,.25,.055)),"Purchase",new(item.Name,"materials_detail",MaterialMarketRoutes.All,"购买数量",MaterialMarketRoutes.All));
 }
 public static NavigationStep Close()=>Step("close","Purchase",new("关闭","materials_close",MaterialMarketRoutes.All,"购买数量",MaterialMarketRoutes.All),"KeyMarket");
}
