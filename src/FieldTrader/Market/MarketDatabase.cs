using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FieldTrader.Market;
public sealed record PriceSummary(int Samples, decimal Minimum, decimal Maximum, decimal Mean, decimal Median, decimal P25, decimal P75);
public sealed record HourlyPriceSummary(int Weekday, int Hour, PriceSummary Prices);
public sealed record Coverage(int Known, int Visited, int Candidates, int Errors);
public sealed record ScanHistory(string ItemId, DateTimeOffset? LastSuccess, DateTimeOffset? LastAttempt, int Failures);
public sealed record HistoricalPrice(string EvidenceId,string ItemId,decimal Price,int? Quantity,DateTimeOffset At,PriceSource Source,string Context);

public sealed partial class MarketDatabase {
    private readonly string connectionString;
    public MarketDatabase(string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 5 }.ToString();
        using var db = Open();
        using var version = db.CreateCommand(); version.CommandText = "PRAGMA user_version";
        int schemaVersion=Convert.ToInt32(version.ExecuteScalar());
        if (schemaVersion > 4) throw new InvalidDataException("市场数据库版本过新");
        if(schemaVersion==1){using var migrate=db.CreateCommand();migrate.CommandText="ALTER TABLE PriceObservations ADD COLUMN DetectionConfidence REAL NOT NULL DEFAULT 1; PRAGMA user_version=2;";migrate.ExecuteNonQuery();}
        using var schema = db.CreateCommand(); schema.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS ItemCatalog(
              Id TEXT PRIMARY KEY, Name TEXT NOT NULL, NormalizedName TEXT NOT NULL UNIQUE, Category TEXT NOT NULL,
              Material INTEGER NOT NULL, Product INTEGER NOT NULL, Special TEXT NOT NULL, Confirmed INTEGER NOT NULL,
              LastVisited TEXT, LastAttempt TEXT, Failures INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS PriceObservations(
              Id INTEGER PRIMARY KEY, ItemId TEXT NOT NULL REFERENCES ItemCatalog(Id), Price INTEGER NOT NULL CHECK(Price>0),
              Quantity INTEGER, ObservedAt TEXT NOT NULL, Confidence REAL NOT NULL, FrameId TEXT NOT NULL,
              ContentHash TEXT NOT NULL, RegionKey TEXT NOT NULL, Context TEXT NOT NULL, Source TEXT NOT NULL,
              Replay INTEGER NOT NULL, Weekday INTEGER NOT NULL, Hour INTEGER NOT NULL, DedupKey TEXT NOT NULL UNIQUE, DetectionConfidence REAL NOT NULL,
              UNIQUE(ContentHash,RegionKey,Context,Source,Replay));
            CREATE INDEX IF NOT EXISTS IX_Prices_ItemTime ON PriceObservations(ItemId,ObservedAt);
            CREATE TABLE IF NOT EXISTS MarketErrors(Id INTEGER PRIMARY KEY, At TEXT NOT NULL, FrameId TEXT, ItemId TEXT, Message TEXT NOT NULL);
            CREATE VIEW IF NOT EXISTS PriceStatistics AS
              SELECT ItemId,Context,Source,Replay,MIN(Price) AS Minimum,MAX(Price) AS Maximum,AVG(Price) AS Mean,COUNT(*) AS Samples
              FROM PriceObservations GROUP BY ItemId,Context,Source,Replay;
            CREATE VIEW IF NOT EXISTS HourlyPriceStatistics AS
              SELECT ItemId,Context,Source,Replay,Weekday,Hour,MIN(Price) AS Minimum,MAX(Price) AS Maximum,AVG(Price) AS Mean,COUNT(*) AS Samples
              FROM PriceObservations GROUP BY ItemId,Context,Source,Replay,Weekday,Hour;
            CREATE TABLE IF NOT EXISTS SeedImports(
              Source TEXT PRIMARY KEY, Revision TEXT NOT NULL, FileHash TEXT NOT NULL, FetchedAt TEXT NOT NULL,
              ImportedAt TEXT NOT NULL, ItemCount INTEGER NOT NULL, PriceCount INTEGER NOT NULL, SourceUrl TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS SeedItemSources(
              Source TEXT NOT NULL REFERENCES SeedImports(Source), ExternalKey TEXT NOT NULL, ItemId TEXT NOT NULL REFERENCES ItemCatalog(Id),
              SourceName TEXT NOT NULL, SourceCategory TEXT NOT NULL, SourceUrl TEXT NOT NULL, FetchedAt TEXT NOT NULL,
              SourceUpdatedAt TEXT, ReferencePrice INTEGER, RawPrice TEXT NOT NULL,
              ResaleEligibility TEXT NOT NULL DEFAULT 'Unknown', RecipeStatus TEXT NOT NULL DEFAULT 'Unverified',
              PRIMARY KEY(Source,ExternalKey));
            PRAGMA user_version=3;
            """; schema.ExecuteNonQuery();
        InitializeTrading();
        InitializeUnifiedProfit();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); using var c = db.CreateCommand(); c.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; c.ExecuteNonQuery(); return db; }
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string, object?)[] values) {
        var c = db.CreateCommand(); c.CommandText = sql;
        foreach (var (name, value) in values) c.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return c;
    }
    public void UpsertItem(CatalogItem item) {
        if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 150) throw new InvalidDataException("无效商品目录");
        var canonical=ResolveCanonical(item.Name);
        if(canonical is not null&&canonical.Id!=item.Id)item=item with{Id=canonical.Id,Name=canonical.Name};
        using var db = Open(); Upsert(db, item);
    }
    private static void Upsert(SqliteConnection db, CatalogItem item) {
        using var c = Command(db, """
            INSERT INTO ItemCatalog(Id,Name,NormalizedName,Category,Material,Product,Special,Confirmed) VALUES($id,$name,$normalized,$cat,$mat,$prod,$spec,$confirmed)
            ON CONFLICT(NormalizedName) DO UPDATE SET Confirmed=MAX(ItemCatalog.Confirmed,excluded.Confirmed),
              Category=CASE WHEN excluded.Confirmed=1 THEN excluded.Category ELSE ItemCatalog.Category END,
              Material=CASE WHEN excluded.Confirmed=1 THEN excluded.Material ELSE ItemCatalog.Material END,
              Product=CASE WHEN excluded.Confirmed=1 THEN excluded.Product ELSE ItemCatalog.Product END,
              Special=CASE WHEN excluded.Confirmed=1 THEN excluded.Special ELSE ItemCatalog.Special END
            """, ("$id", item.Id), ("$name", item.Name), ("$normalized", Geometry.Normalize(item.Name)), ("$cat", item.Category),
            ("$mat", item.Material), ("$prod", item.Product), ("$spec", item.Special), ("$confirmed", item.Confirmed)); c.ExecuteNonQuery();
    }
    public CatalogItem[] Catalog() {
        using var db = Open(); using var c = Command(db, "SELECT Id,Name,Category,Material,Product,Special,Confirmed,LastVisited FROM ItemCatalog ORDER BY Name");
        using var r = c.ExecuteReader(); var list = new List<CatalogItem>();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4), r.GetString(5), r.GetBoolean(6), r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture)));
        return list.ToArray();
    }
    public int Store(PerceptionResult result, bool replay) {
        using var db = Open(); using var tx = db.BeginTransaction(); int inserted = 0;
        foreach (var q in result.Quotes) {
            if (q.Price <= 0 || q.Price > 1_000_000_000_000m || q.Price != decimal.Truncate(q.Price) || q.Quantity is <= 0 || q.Confidence < OcrPolicy.MinimumConfidence) continue;
            string normalized = Geometry.Normalize(q.ItemName);
            var canonical=ResolveCanonical(q.ItemName);
            using var lookup = Command(db, "SELECT Id FROM ItemCatalog WHERE NormalizedName=$name", ("$name", normalized)); lookup.Transaction = tx;
            string? id = canonical?.Id ?? lookup.ExecuteScalar() as string;
            if (id is null) {
                id = "candidate-" + Hash(normalized)[..20];
                using var add = Command(db, "INSERT INTO ItemCatalog(Id,Name,NormalizedName,Category,Material,Product,Special,Confirmed) VALUES($id,$name,$norm,'未分类',0,0,'none',0)",
                    ("$id", id), ("$name", q.ItemName), ("$norm", normalized)); add.Transaction = tx; add.ExecuteNonQuery();
            }
            var at = result.Vision.CapturedAt;
            // Game-hour analysis explicitly uses China Standard Time. UTC stored as canonical text.
            var local = at.ToOffset(TimeSpan.FromHours(8));
            var fingerprint = Hash(FormattableString.Invariant($"{id}|{q.Price}|{q.Quantity}|{q.Context}|{at.ToUnixTimeSeconds()/300}|{replay}"));
            using var insert = Command(db, """
                INSERT OR IGNORE INTO PriceObservations(ItemId,Price,Quantity,ObservedAt,Confidence,FrameId,ContentHash,RegionKey,Context,Source,Replay,Weekday,Hour,DedupKey,DetectionConfidence)
                VALUES($item,$price,$qty,$at,$confidence,$frame,$hash,$region,$context,'OcrObservation',$replay,$day,$hour,$dedup,$detConfidence)
                """, ("$item", id), ("$price", (long)q.Price), ("$qty", q.Quantity), ("$at", at.ToUniversalTime().ToString("O")),
                ("$confidence", q.Confidence), ("$frame", result.Vision.FrameId), ("$hash", result.Vision.ContentHash), ("$region", q.RegionKey),
                ("$context", q.Context), ("$replay", replay), ("$day", (int)local.DayOfWeek), ("$hour", local.Hour), ("$dedup", fingerprint),("$detConfidence",q.DetectionConfidence));
            insert.Transaction = tx; inserted += insert.ExecuteNonQuery();
            if (!replay) {
                using var visited = Command(db, "UPDATE ItemCatalog SET LastVisited=$at,LastAttempt=$at,Failures=0 WHERE Id=$id AND Confirmed=1", ("$at", at.ToUniversalTime().ToString("O")), ("$id", id));
                visited.Transaction = tx; visited.ExecuteNonQuery();
            }
        }
        tx.Commit();
        foreach (var issue in result.Issues) Log(result.Vision.FrameId, null, issue);
        return inserted;
    }
    public bool ImportHistorical(HistoricalPrice price) {
        // Imported fills require a receipt/evidence ID. OCR observations only enter through Store.
        if(price.Source==PriceSource.OcrObservation||!Enum.IsDefined(price.Source)||string.IsNullOrWhiteSpace(price.EvidenceId)
            ||price.Price<=0||price.Price>1_000_000_000_000m||price.Price!=decimal.Truncate(price.Price)||price.Quantity is <=0||string.IsNullOrWhiteSpace(price.Context))
            throw new InvalidDataException("历史价格需要明确来源、凭证ID、上下文和有效金额");
        using var db=Open();var local=price.At.ToOffset(TimeSpan.FromHours(8));
        string evidence=Hash(price.Source+":"+price.EvidenceId+":"+price.ItemId);
        using var c=Command(db,"""
            INSERT OR IGNORE INTO PriceObservations(ItemId,Price,Quantity,ObservedAt,Confidence,FrameId,ContentHash,RegionKey,Context,Source,Replay,Weekday,Hour,DedupKey,DetectionConfidence)
            VALUES($id,$price,$qty,$at,1,$evidence,$evidence,'import',$context,$source,0,$day,$hour,$evidence,1)
            """,("$id",price.ItemId),("$price",(long)price.Price),("$qty",price.Quantity),("$at",price.At.ToUniversalTime().ToString("O")),
            ("$evidence",evidence),("$context",price.Context),("$source",price.Source.ToString()),("$day",(int)local.DayOfWeek),("$hour",local.Hour));
        return c.ExecuteNonQuery()==1;
    }
    public void Log(string? frame, string? item, string message) {
        using var db = Open(); using var c = Command(db, "INSERT INTO MarketErrors(At,FrameId,ItemId,Message) VALUES($at,$frame,$item,$message)",
            ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$frame", frame), ("$item", item), ("$message", message)); c.ExecuteNonQuery();
    }
    public void FailedAttempt(string id, DateTimeOffset now, string reason) {
        using var db = Open(); using var c = Command(db, "UPDATE ItemCatalog SET LastAttempt=$at,Failures=Failures+1 WHERE Id=$id", ("$at", now.ToUniversalTime().ToString("O")), ("$id", id)); c.ExecuteNonQuery(); Log(null,id,reason);
    }
    public ScanHistory[] History() {
        using var db = Open(); using var c = Command(db, "SELECT Id,LastVisited,LastAttempt,Failures FROM ItemCatalog WHERE Confirmed=1"); using var r = c.ExecuteReader();
        var list = new List<ScanHistory>(); while(r.Read()) list.Add(new(r.GetString(0), r.IsDBNull(1)?null:DateTimeOffset.Parse(r.GetString(1)), r.IsDBNull(2)?null:DateTimeOffset.Parse(r.GetString(2)),r.GetInt32(3))); return list.ToArray();
    }
    public Coverage Coverage(DateTimeOffset since) {
        using var db = Open(); using var c = Command(db, "SELECT COUNT(*),COALESCE(SUM(CASE WHEN LastVisited >= $since THEN 1 ELSE 0 END),0) FROM ItemCatalog WHERE Confirmed=1", ("$since",since.ToUniversalTime().ToString("O")));
        using var r=c.ExecuteReader();r.Read();int known=r.GetInt32(0),visited=r.GetInt32(1);r.Close();
        using var pending=Command(db,"SELECT COUNT(*) FROM ItemCatalog WHERE Confirmed=0");using var errors=Command(db,"SELECT COUNT(*) FROM MarketErrors");
        return new(known,visited,Convert.ToInt32(pending.ExecuteScalar()),Convert.ToInt32(errors.ExecuteScalar()));
    }
    public PriceSummary Statistics(string item, string context, PriceSource source=PriceSource.OcrObservation, bool replay=false) => Summarize(Prices(item,context,source,replay).Select(p=>p.Price));
    public HourlyPriceSummary[] Hourly(string item,string context, PriceSource source=PriceSource.OcrObservation,bool replay=false) =>
        Prices(item,context,source,replay).GroupBy(p=>(p.Day,p.Hour)).Select(g=>new HourlyPriceSummary(g.Key.Day,g.Key.Hour,Summarize(g.Select(p=>p.Price)))).ToArray();
    private List<(decimal Price,int Day,int Hour)> Prices(string item,string context,PriceSource source,bool replay) {
        using var db=Open();using var c=Command(db,"SELECT Price,Weekday,Hour FROM PriceObservations WHERE ItemId=$id AND Context=$context AND Source=$source AND Replay=$replay", ("$id",item),("$context",context),("$source",source.ToString()),("$replay",replay));
        using var r=c.ExecuteReader();var list=new List<(decimal,int,int)>();while(r.Read())list.Add((r.GetInt64(0),r.GetInt32(1),r.GetInt32(2)));return list;
    }
    public static PriceSummary Summarize(IEnumerable<decimal> values) {
        var a=values.Order().ToArray();if(a.Length==0)return new(0,0,0,0,0,0,0);
        decimal Q(decimal q){decimal pos=(a.Length-1)*q;int low=(int)pos;int high=Math.Min(low+1,a.Length-1);return a[low]+(a[high]-a[low])*(pos-low);}
        return new(a.Length,a[0],a[^1],a.Average(),Q(.5m),Q(.25m),Q(.75m));
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
