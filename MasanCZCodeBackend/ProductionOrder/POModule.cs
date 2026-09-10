using MasanCZCodeBackend.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MasanCZCodeBackend.ProductionOrder;

/// <summary>
/// Thông tin một Production Order — mirror <c>POInfo</c> trong file gốc <c>CProject/POModule.cs</c>.
/// </summary>
public class POInfo
{
    public string orderNo { get; set; } = "-";
    public string site { get; set; } = "-";
    public string factory { get; set; } = "-";
    public string productionLine { get; set; } = "-";
    public string productionDate { get; set; } = "-";
    public string shift { get; set; } = "-";
    public string orderQty { get; set; } = "-";
    public string lotNumber { get; set; } = "-";
    public string productCode { get; set; } = "-";
    public string productName { get; set; } = "-";
    public string gtin { get; set; } = "-";
    public string customerOrderNo { get; set; } = "-";
    public string uom { get; set; } = "-";
    public string packSize { get; set; } = "-";
    public string totalCZCode { get; set; } = "-";
    public Product_Counter Counter { get; set; } = new Product_Counter();

    // Lưu danh sách thùng + sản phẩm cho tiện xử lý runtime
    public Dictionary<string, CartonInfo> CartonInfo { get; set; } = new();
    public Dictionary<string, ProductInfo> ProductInfo { get; set; } = new();
}

public class Product_Counter
{
    public int totalCount { get; set; } = 0;     // tổng số sản phẩm
    public int passCount { get; set; } = 0;      // số tốt
    public int failCount { get; set; } = 0;      // số xấu
    public int duplicateCount { get; set; } = 0; // trùng
    public int readfailCount { get; set; } = 0;
    public int notfoundCount { get; set; } = 0;
    public int errorCount { get; set; } = 0;
    public int formatErrorCount { get; set; } = 0;
}

public class CartonInfo
{
    public string carton_Code { get; set; } = "0";
    public string carton_Start_Time { get; set; } = "0";
    public string carton_End_Time { get; set; } = "0";
}

public class ProductInfo
{
    public string product_Code { get; set; } = "0";
    public string product_Start_Time { get; set; } = "0";
    public string product_End_Time { get; set; } = "0";
}

/// <summary>
/// Quản lý database <c>PO_List.db</c> chứa danh sách Production Order.
/// Port từ file gốc <c>CProject/Module/POModule.cs</c> (class <c>PODatabase</c>).
/// <para>
/// Concurrency:
/// <list type="bullet">
/// <item><c>PRAGMA journal_mode=WAL</c> + <c>busy_timeout=5000</c> + <c>synchronous=NORMAL</c> — bật trong <c>InitializeDatabase</c>.</item>
/// <item><c>SemaphoreSlim(1,1)</c> cho <c>CreateProductionOrder</c> (thử-wait 0ms; bận thì fail ngay với "PO database đang bận, thử lại sau").</item>
/// <item>Read operations không lock (nhờ WAL).</item>
/// </list>
/// </para>
/// </summary>
public class PODatabase
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly int _busyTimeoutMs;
    private readonly SemaphoreSlim _writeLock;

    public PODatabase(IOptions<StorageOptions> options)
    {
        var opt = options.Value;
        _busyTimeoutMs = opt.BusyTimeoutMs > 0 ? opt.BusyTimeoutMs : 5000;
        _dbPath = Path.Combine(opt.PoDatabasePath, "PO_List.db");

        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = $"Data Source={_dbPath}";
        _writeLock = new SemaphoreSlim(1, 1);
    }

    private void ApplyPragmas(SqliteConnection con)
    {
        using (var pragma = new SqliteCommand($"PRAGMA busy_timeout={_busyTimeoutMs};", con))
        {
            pragma.ExecuteNonQuery();
        }
        using (var pragma = new SqliteCommand("PRAGMA synchronous=NORMAL;", con))
        {
            pragma.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Tạo bảng ProductionOrder nếu chưa có + bật WAL/busy_timeout/synchronous.
    /// </summary>
    public void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Bật WAL ngay khi mở connection đầu tiên (chỉ có tác dụng persistent cho file).
        using (var pragma = new SqliteCommand("PRAGMA journal_mode=WAL;", connection))
        {
            pragma.ExecuteNonQuery();
        }
        ApplyPragmas(connection);

        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS ProductionOrder (
                ID INTEGER NOT NULL UNIQUE,
                orderNo TEXT NOT NULL UNIQUE,
                site TEXT NOT NULL,
                factory TEXT NOT NULL,
                productionLine TEXT NOT NULL,
                productionDate TEXT NOT NULL,
                shift TEXT NOT NULL,
                orderQty INTEGER NOT NULL,
                lotNumber TEXT NOT NULL,
                productCode TEXT NOT NULL,
                productName TEXT NOT NULL,
                gtin TEXT NOT NULL,
                customerOrderNo TEXT NOT NULL,
                uom TEXT NOT NULL,
                packSize INTEGER NOT NULL DEFAULT 24,
                CreateDate TEXT NOT NULL,
                CreateUser TEXT NOT NULL,
                PRIMARY KEY(ID AUTOINCREMENT)
            );";

        using var command = new SqliteCommand(createTableSql, connection);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Insert một PO mới (có kiểm tra trùng <c>orderNo</c>).
    /// Trả về tuple <c>(bool success, string message, int id)</c> — giữ signature từ file gốc.
    /// Khi PO database đang bận thì trả ngay <c>(false, "PO database đang bận, thử lại sau", -1)</c>.
    /// </summary>
    public (bool success, string message, int id) CreateProductionOrder(POInfo po, string createUser)
    {
        if (po == null)
        {
            return (false, "POInfo is null", -1);
        }

        // Thử lấy lock với wait=0 — nếu đang bận thì fail ngay.
        if (!_writeLock.Wait(0))
        {
            return (false, "PO database đang bận, thử lại sau", -1);
        }

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            ApplyPragmas(connection);

            // Check if orderNo already exists
            var checkSql = "SELECT COUNT(*) FROM ProductionOrder WHERE orderNo = @orderNo";
            using var checkCmd = new SqliteCommand(checkSql, connection);
            checkCmd.Parameters.AddWithValue("@orderNo", po.orderNo ?? string.Empty);
            var exists = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;

            if (exists)
            {
                return (false, $"OrderNo '{po.orderNo}' already exists", -1);
            }

            var insertSql = @"
                INSERT INTO ProductionOrder
                (orderNo, site, factory, productionLine, productionDate, shift,
                 orderQty, lotNumber, productCode, productName, gtin,
                 customerOrderNo, uom, packSize, CreateDate, CreateUser)
                VALUES (@orderNo, @site, @factory, @productionLine, @productionDate, @shift,
                        @orderQty, @lotNumber, @productCode, @productName, @gtin,
                        @customerOrderNo, @uom, @packSize, @createDate, @createUser);
                SELECT last_insert_rowid();";

            using var insertCmd = new SqliteCommand(insertSql, connection);
            insertCmd.Parameters.AddWithValue("@orderNo", po.orderNo ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@site", po.site ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@factory", po.factory ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@productionLine", po.productionLine ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@productionDate", po.productionDate ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@shift", po.shift ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@orderQty", int.TryParse(po.orderQty, out var qty) ? qty : 0);
            insertCmd.Parameters.AddWithValue("@lotNumber", po.lotNumber ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@productCode", po.productCode ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@productName", po.productName ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@gtin", po.gtin ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@customerOrderNo", po.customerOrderNo ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@uom", po.uom ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@packSize", int.TryParse(po.packSize, out var ps) ? ps : 24);
            insertCmd.Parameters.AddWithValue("@createDate", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            insertCmd.Parameters.AddWithValue("@createUser", createUser ?? string.Empty);

            var id = Convert.ToInt32(insertCmd.ExecuteScalar());
            return (true, "PO created successfully", id);
        }
        catch (Exception ex)
        {
            return (false, $"Lỗi khi tạo PO: {ex.Message}", -1);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
