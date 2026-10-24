using System.Globalization;
using System.Text.RegularExpressions;

namespace FieldTrader.Market;
public enum PriceSource { Reference, OcrObservation, ActualFill }
public sealed record CatalogItem(string Id, string Name, string Category = "未分类", bool Material = false,
    bool Product = false, string Special = "none", bool Confirmed = false, DateTimeOffset? LastVisited = null);
public sealed record ObservedQuote(string ItemName, decimal Price, int? Quantity, double Confidence, string RegionKey,
    string Context, bool CatalogMatched, double DetectionConfidence = 1);
public sealed record PerceptionResult(VisionResult Vision, MarketPage Page, bool Paused, string Reason,
    ObservedQuote[] Quotes, string[] Issues);

public static class MarketPerception {
    private static readonly string[] PauseWords = ["验证码", "安全验证", "反作弊", "封禁", "异常环境", "验证失败"];
    public static PerceptionResult Fuse(VisionResult v, IReadOnlyList<CatalogItem> catalog) {
        var texts = v.Texts.Where(t => t.Confidence >= .90 && t.Bounds.Valid).ToArray();
        var regions = v.Regions.Where(t => t.Confidence >= .6 && t.Bounds.Valid).ToArray();
        if (texts.Any(t => PauseWords.Any(w => t.Label.Contains(w, StringComparison.Ordinal))))
            return new(v, MarketPage.Unknown, true, "检测到安全验证或异常提示", [], []);
        bool Has(string s) => texts.Any(t => Geometry.Normalize(t.Label) == Geometry.Normalize(s));
        var trade = regions.Where(t => t.Label == "trade_panel").ToArray();
        // Detail detection alone may be weak; OCR still requires a unique title inside it.
        var detail = v.Regions.Where(t => t.Label == "detail_panel"&&t.Confidence>=.35&&t.Bounds.Valid).ToArray();
        bool modal = trade.Length > 0 || regions.Any(t => t.Label == "confirm_dialog");
        var page = MarketPage.Unknown;
        var confirms=regions.Where(t=>t.Label=="confirm_dialog").ToArray();
        if(Has("配件列表")&&texts.Any(t=>t.Label.Contains("出售总价格")))page=MarketPage.SellBundle;
        else if(confirms.Length==1 && texts.Any(t=>Geometry.Contains(confirms[0].Bounds,t.Bounds)&&t.Label.Contains("兑换")))page=MarketPage.ExchangeConfirm;
        else if(trade.Length==1 && texts.Any(t=>Geometry.Contains(trade[0].Bounds,t.Bounds)&&Geometry.Normalize(t.Label).Contains("道具兑换")))page=MarketPage.Exchange;
        else if (trade.Length == 1 && detail.Length == 1) {
            var panelTexts = texts.Where(t => Geometry.Contains(trade[0].Bounds, t.Bounds)).ToArray();
            if (panelTexts.Any(t => Geometry.Normalize(t.Label) == "购买数量") && panelTexts.Any(t => Geometry.Normalize(t.Label) == "平均单价")) page = MarketPage.Purchase;
            else if (panelTexts.Any(t => Geometry.Normalize(t.Label) == "出售数量") && panelTexts.Any(t => Geometry.Normalize(t.Label) == "出售价格")) page = MarketPage.Sell;
        } else if (!modal && regions.Any(t=>t.Label=="result_region")&&Has("购买结果")&&texts.Any(t=>t.Label.Contains("已购买"))) page=MarketPage.Receipt;
        else if(!modal&&regions.Any(t=>t.Label=="listed_item")&&texts.Any(t=>t.Bounds.Y<.17&&t.Label.Contains("我的上架物品")))page=MarketPage.Listings;
        else if (!modal && (regions.Any(t => t.Label == "product_card")||regions.Any(t=>t.Label=="market_entry")&&Has("暂无收藏")&&Has("输入物品名称搜索")) && Has("购买") && (Has("我的交易") || Has("市场"))) page = MarketPage.Market;
        else if (!modal && regions.Any(t => t.Label == "material_card" || t.Label == "material_row") && Has("联络人")) page = MarketPage.Contact;
        var quotes = new List<ObservedQuote>(); var issues = new List<string>();
        bool Known(string name) => catalog.Any(i => i.Confirmed && Geometry.Normalize(i.Name) == Geometry.Normalize(name));
        if (page is MarketPage.Purchase or MarketPage.Sell) {
            var panel = trade[0].Bounds;
            var names = texts.Where(t => Geometry.Contains(detail[0].Bounds, t.Bounds)
                && t.Bounds.Y < detail[0].Bounds.Y + detail[0].Bounds.Height * .12).OrderBy(t => t.Bounds.Y).ToArray();
            var title = names.FirstOrDefault(t => t.Label.Any(char.IsLetter));
            var price = page == MarketPage.Purchase ? AlignedNumber(texts, panel, "平均单价") : FieldNumber(texts, regions, panel, "price_field");
            var quantity = FieldNumber(texts, regions, panel, "quantity_field");
            if (title is not null && price is not null && (quantity is null || quantity.Value.Value <= int.MaxValue)) {
                quotes.Add(new(title.Label, price.Value.Value, quantity is null?null:(int)quantity.Value.Value,
                    new[] { title.Confidence, price.Value.Confidence, quantity?.Confidence??1 }.Min(),
                    "active-dialog", page == MarketPage.Purchase ? "purchase-unit" : "sell-displayed", Known(title.Label), Math.Min(trade[0].Confidence,detail[0].Confidence)));
                if(quantity is null)issues.Add("弹窗数量未识别，保存为未知；该报价不能直接执行交易");
            } else issues.Add("弹窗商品标题或单价未唯一关联；未写入报价");
        }
        if (page == MarketPage.Market) {
            var cards = regions.Where(t => t.Label == "product_card").ToArray();
            foreach (var card in cards) {
                var inside = texts.Where(t => Geometry.Contains(card.Bounds, t.Bounds)).ToArray();
                // Only title area and bottom price area; never associate adjacent-card values.
                var titles = inside.Where(t => t.Bounds.Y < card.Bounds.Y + card.Bounds.Height * .25 && t.Label.Any(char.IsLetter)).ToArray();
                var numbers = inside.Where(t => t.Bounds.Y > card.Bounds.Y + card.Bounds.Height * .65 && TryInteger(t.Label, out _)).ToArray();
                if (titles.Length != 1 || numbers.Length != 1 || cards.Count(c => Geometry.Contains(c.Bounds, numbers[0].Bounds)) != 1) {
                    issues.Add("商品卡片标题/价格不唯一或重叠：" + Key(card.Bounds)); continue;
                }
                TryInteger(numbers[0].Label, out var price);
                quotes.Add(new(titles[0].Label, price, null, Math.Min(titles[0].Confidence, numbers[0].Confidence),
                    Key(card.Bounds), "market-card", Known(titles[0].Label),card.Confidence));
            }
        }
        if (page == MarketPage.Unknown) issues.Add("未知页面；仅保存原始识别结果");
        return new(v, page, false, "", quotes.ToArray(), issues.ToArray());
    }
    private static string Key(Region r) => FormattableString.Invariant($"{r.X:F2}:{r.Y:F2}:{r.Width:F2}:{r.Height:F2}");
    public static bool TryInteger(string text, out decimal value) {
        text = Geometry.Normalize(text); value = 0;
        // No guessing K, decimal point, currency glyph, or OCR letter-to-digit corrections.
        return Regex.IsMatch(text, @"^(?:[1-9]\d{0,9}|[1-9]\d{0,2}(?:,\d{3}){1,3})$")
            && decimal.TryParse(text, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value) && value > 0;
    }
    private static (decimal Value, double Confidence)? FieldNumber(VisualToken[] texts, VisualToken[] regions, Region panel, string kind) {
        var fields = regions.Where(r => r.Label == kind && Geometry.Contains(panel, r.Bounds)).ToArray();
        if (fields.Length != 1) return null;
        var found = texts.Where(t => Geometry.Contains(fields[0].Bounds, t.Bounds) && TryInteger(t.Label, out _)).ToArray();
        if (found.Length != 1) return null;
        TryInteger(found[0].Label, out var value); return (value, found[0].Confidence);
    }
    private static (decimal Value, double Confidence)? AlignedNumber(VisualToken[] texts, Region panel, string label) {
        var labels = texts.Where(t => Geometry.Contains(panel, t.Bounds) && Geometry.Normalize(t.Label) == label).ToArray();
        if (labels.Length != 1) return null;
        var found = texts.Where(t => Geometry.Contains(panel, t.Bounds) && t.Bounds.X > labels[0].Bounds.X + labels[0].Bounds.Width
            && Math.Abs(Geometry.CenterY(t.Bounds) - Geometry.CenterY(labels[0].Bounds)) < Math.Max(t.Bounds.Height, labels[0].Bounds.Height) * .6
            && TryInteger(t.Label, out _)).ToArray();
        if (found.Length != 1) return null;
        TryInteger(found[0].Label, out var value); return (value, Math.Min(found[0].Confidence, labels[0].Confidence));
    }
}
