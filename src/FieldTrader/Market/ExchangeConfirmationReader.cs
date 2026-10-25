namespace FieldTrader.Market;

// Recognition only. A match does not grant input permission or prove completion.
public sealed record ExchangeConfirmation(string ItemId,string ItemName,string RawPrompt,Region ConfirmTextBounds,double Confidence);
public static class ExchangeConfirmationReader {
    public const string Prefix="已集齐全部兑换道具是否兑换";
    private static string Key(string value)=>new(Geometry.Normalize(value).Where(char.IsLetterOrDigit).ToArray());
    private static bool NameMatches(string raw,string expected) {
        var name=Key(raw);var target=Key(expected);
        // Observed PP-OCR confusion, scoped to this exact item rather than global digit replacement.
        return name==target || target==Key("100D战地医疗包")&&name==Key("1000战地医疗包");
    }
    public static ExchangeConfirmation? Read(VisionResult vision,IReadOnlyList<CatalogItem> catalog) {
        var texts=vision.Texts.Where(t=>t.Confidence >= OcrPolicy.MinimumConfidence&&t.Bounds.Valid).ToArray();
        var prompts=texts.Where(t=>t.Bounds.X>.15&&t.Bounds.Y>.25&&t.Bounds.Y<.7&&Key(t.Label).StartsWith(Prefix)).ToArray();
        if(prompts.Length!=1)return null;
        var prompt=prompts[0];var suffix=Key(prompt.Label)[Prefix.Length..];
        var items=catalog.Where(i=>i.Confirmed&&i.Product&&NameMatches(suffix,i.Name)).DistinctBy(i=>i.Id).ToArray();
        if(items.Length!=1)return null;
        var yes=texts.Where(t=>Key(t.Label)=="确定"&&t.Bounds.Y>prompt.Bounds.Y+prompt.Bounds.Height&&t.Bounds.Y<prompt.Bounds.Y+.12&&t.Bounds.X>.48&&t.Bounds.X<.7).ToArray();
        var no=texts.Where(t=>Key(t.Label)=="取消"&&t.Bounds.Y>prompt.Bounds.Y+prompt.Bounds.Height&&t.Bounds.Y<prompt.Bounds.Y+.12&&t.Bounds.X>.25&&t.Bounds.X<.5).ToArray();
        if(yes.Length!=1||no.Length!=1||Math.Abs(Geometry.CenterY(yes[0].Bounds)-Geometry.CenterY(no[0].Bounds))>.025)return null;
        return new(items[0].Id,items[0].Name,prompt.Label,yes[0].Bounds,Math.Min(prompt.Confidence,Math.Min(yes[0].Confidence,no[0].Confidence)));
    }
}
