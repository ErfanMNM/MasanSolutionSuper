using System.Data;
using MasanCZCodeBackend.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MasanCZCodeBackend.DataPool;

/// <summary>
/// Module quản lý các Pool (DataPool): mỗi Pool là 1 file .db trong <c>StorageOptions.DataPoolPath</c>.
/// Port từ file gốc <c>CProject/Module/DataPoolModule.cs</c>, giữ nguyên schema/method/signature.
/// <para>
/// Concurrency: không dùng <c>SemaphoreSlim</c> per-pool. Phụ thuộc vào:
/// <list type="bullet">
/// <item><c>PRAGMA journal_mode=WAL</c> — 1 writer + nhiều reader đồng thời.</item>
/// <item><c>PRAGMA busy_timeout=5000</c> — chờ 5s nếu DB bận, sau đó throw <c>SqliteException</c>.</item>
/// <item><c>PRAGMA synchronous=NORMAL</c> — tăng tốc commit, an toàn với WAL.</item>
/// <item>Connection pooling mặc định của <c>Microsoft.Data.Sqlite</c>.</item>
/// </list>
/// </para>
/// </summary>
public class DataPoolModule
{
    private readonly string _databasePath;
    private readonly int _busyTimeoutMs;

    public DataPoolModule(IOptions<StorageOptions> options)
    {
        var opt = options.Value;
        _databasePath = opt.DataPoolPath;
        _busyTimeoutMs = opt.BusyTimeoutMs > 0 ? opt.BusyTimeoutMs : 5000;

        if (!Directory.Exists(_databasePath))
        {
            Directory.CreateDirectory(_databasePath);
        }
    }

    // ---------- Helpers ----------

    private void ApplyPragmas(SqliteConnection con)
    {
        // WAL, busy_timeout, synchronous=NORMAL — chỉ set cho connection này (kế thừa từ file gốc journal_mode=WAL).
        using (var pragma = new SqliteCommand($"PRAGMA busy_timeout={_busyTimeoutMs};", con))
        {
            pragma.ExecuteNonQuery();
        }
        using (var pragma = new SqliteCommand("PRAGMA synchronous=NORMAL;", con))
        {
            pragma.ExecuteNonQuery();
        }
    }

    // ---------- Path / Create ----------

    /// <summary>Lấy đường dẫn file .db của pool theo tên.</summary>
    public DataPoolResultString GetPoolPath(string poolName)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            return new DataPoolResultString(false, "Tên Pool không được trống.", string.Empty);
        }
        if (poolName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return new DataPoolResultString(false, "Tên Pool chứa ký tự không hợp lệ (path/file).", string.Empty);
        }
        return new DataPoolResultString(true, "Success", Path.Combine(_databasePath, poolName + ".db"));
    }

    /// <summary>Tạo pool mới + schema Pool / Codes / Indexes + bật WAL.</summary>
    public DataPoolResultString CreatePool(PoolInfo poolInfo)
    {
        if (poolInfo == null)
        {
            return new DataPoolResultString(false, "Không hợp lệ, class PoolInfo là null", string.Empty);
        }
        if (string.IsNullOrWhiteSpace(poolInfo.PoolName))
        {
            return new DataPoolResultString(false, "Không hợp lệ, PoolName là rỗng", string.Empty);
        }
        string poolPath = GetPoolPath(poolInfo.PoolName).Data;
        if (File.Exists(poolPath))
        {
            return new DataPoolResultString(false, "Pool đã tồn tại", string.Empty);
        }
        if (!Directory.Exists(_databasePath))
        {
            Directory.CreateDirectory(_databasePath);
        }

        const string sql = @"
            CREATE TABLE IF NOT EXISTS Pool (
                ID INTEGER PRIMARY KEY AUTOINCREMENT,
                PoolName TEXT NOT NULL UNIQUE,
                PoolDescription TEXT NOT NULL,
                PoolCreateID TEXT NOT NULL,
                PoolNote TEXT NOT NULL,
                PoolCreatedBy TEXT NOT NULL,
                PoolCreateDatetime TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Codes (
                ID INTEGER PRIMARY KEY AUTOINCREMENT,
                PoolCode TEXT NOT NULL UNIQUE,
                Status INTEGER NOT NULL DEFAULT 0,
                PoolCodeUsedBatchID TEXT NOT NULL DEFAULT '',
                PoolCodeUsedDatetime TEXT NOT NULL DEFAULT '',
                PoolCodeNote TEXT NOT NULL DEFAULT '',
                PoolCodeCreateID TEXT NOT NULL DEFAULT '',
                PoolCodeCreatedBy TEXT NOT NULL DEFAULT '',
                PoolCodeCreateDatetime TEXT NOT NULL DEFAULT '',
                PrintStatus INTEGER NOT NULL DEFAULT 0
            );

            -- Indexes
            CREATE INDEX IF NOT EXISTS IDX_Codes_Status ON Codes(Status);
            CREATE INDEX IF NOT EXISTS IDX_Codes_UsedBatchID ON Codes(PoolCodeUsedBatchID);
            CREATE INDEX IF NOT EXISTS IDX_Codes_CreateID ON Codes(PoolCodeCreateID);
            CREATE INDEX IF NOT EXISTS IDX_Codes_CreatedBy ON Codes(PoolCodeCreatedBy);
            CREATE INDEX IF NOT EXISTS IDX_Codes_CreateDatetime ON Codes(PoolCodeCreateDatetime);

            -- Composite indexes
            CREATE INDEX IF NOT EXISTS IDX_Codes_Status_CreateDatetime ON Codes(Status, PoolCodeCreateDatetime);
            CREATE INDEX IF NOT EXISTS IDX_Codes_Batch_Status ON Codes(PoolCodeUsedBatchID, Status);
            CREATE INDEX IF NOT EXISTS IDX_Codes_PrintStatus ON Codes(PrintStatus);
            CREATE INDEX IF NOT EXISTS IDX_Codes_Status_PrintStatus ON Codes(Status, PrintStatus);
            PRAGMA journal_mode=WAL;
        ";

        using var con = new SqliteConnection($"Data Source={poolPath}");
        con.Open();

        using (var cmd = new SqliteCommand(sql, con))
        {
            cmd.ExecuteNonQuery();
        }

        // Apply busy_timeout + synchronous cho các connection sau.
        ApplyPragmas(con);

        string createDatetime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        using (var insertCmd = new SqliteCommand(@"
            INSERT INTO Pool (PoolName, PoolDescription, PoolCreateID, PoolNote, PoolCreatedBy, PoolCreateDatetime)
            VALUES (@name, @description, @createID, @note, @createdBy, @createDatetime)", con))
        {
            insertCmd.Parameters.AddWithValue("@name", poolInfo.PoolName);
            insertCmd.Parameters.AddWithValue("@description", poolInfo.PoolDescription ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@createID", poolInfo.PoolCreateID ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@note", poolInfo.PoolNote ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@createdBy", poolInfo.PoolCreatedBy ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@createDatetime", createDatetime);
            insertCmd.ExecuteNonQuery();
        }

        return new DataPoolResultString(true, "Pool created successfully", poolPath);
    }

    // ---------- AddCodes ----------

    /// <summary>
    /// Thêm mã vào pool. <paramref name="mode"/>: 0 = file CSV, 1 = 1 code, 2 = DataTable.
    /// </summary>
    public DataPoolAddCodesResult AddCodes(
        string poolName,
        int mode,
        string? filePath,
        string? singleCode,
        DataTable? dataTable,
        string createID,
        string createdBy,
        Action<int, int>? progressCallback = null)
    {
        var result = new DataPoolAddCodesResult();

        if (string.IsNullOrWhiteSpace(poolName))
        {
            result.Message = "Tên Pool không được trống.";
            return result;
        }
        if (mode < 0 || mode > 2)
        {
            result.Message = "Mode không hợp lệ. Chỉ chấp nhận 0 (file), 1 (1 code), hoặc 2 (DataTable).";
            return result;
        }

        var poolPathResult = GetPoolPath(poolName);
        if (!poolPathResult.Success)
        {
            result.Message = poolPathResult.Message;
            return result;
        }
        string poolPath = poolPathResult.Data;
        if (!File.Exists(poolPath))
        {
            result.Message = "Pool không tồn tại.";
            return result;
        }

        List<string> codesToAdd = new();

        switch (mode)
        {
            case 0: // File CSV
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    result.Message = "Đường dẫn file không được trống khi mode = 0.";
                    return result;
                }
                if (!File.Exists(filePath))
                {
                    result.Message = $"File không tồn tại: {filePath}";
                    return result;
                }
                try
                {
                    var lines = File.ReadAllLines(filePath);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            codesToAdd.Add(trimmed);
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Message = $"Lỗi khi đọc file: {ex.Message}";
                    return result;
                }
                break;

            case 1: // 1 code
                if (string.IsNullOrWhiteSpace(singleCode))
                {
                    result.Message = "Code không được trống khi mode = 1.";
                    return result;
                }
                codesToAdd.Add(singleCode.Trim());
                break;

            case 2: // DataTable
                if (dataTable == null || dataTable.Rows.Count == 0)
                {
                    result.Message = "DataTable không hợp lệ hoặc không có dữ liệu khi mode = 2.";
                    return result;
                }
                foreach (DataRow row in dataTable.Rows)
                {
                    var code = row[0]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(code))
                    {
                        codesToAdd.Add(code);
                    }
                }
                break;
        }

        if (codesToAdd.Count == 0)
        {
            result.Message = "Không có code nào để thêm.";
            return result;
        }

        result.TotalCount = codesToAdd.Count;
        string createDatetime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using var con = new SqliteConnection($"Data Source={poolPath}");
        con.Open();
        ApplyPragmas(con);

        using var transaction = con.BeginTransaction();
        try
        {
            int processed = 0;
            using var insertCmd = new SqliteCommand(@"
                INSERT OR IGNORE INTO Codes (PoolCode, Status, PoolCodeCreateID, PoolCodeCreatedBy, PoolCodeCreateDatetime, PrintStatus)
                VALUES (@code, 0, @createID, @createdBy, @createDatetime, 0)", con, transaction);
            insertCmd.Parameters.AddWithValue("@createID", createID ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@createdBy", createdBy ?? string.Empty);
            insertCmd.Parameters.AddWithValue("@createDatetime", createDatetime);
            var codeParam = insertCmd.Parameters.Add("@code", SqliteType.Text);

            foreach (var code in codesToAdd)
            {
                if (string.IsNullOrWhiteSpace(code)) continue;
                try
                {
                    codeParam.Value = code;
                    int rowsAffected = insertCmd.ExecuteNonQuery();
                    if (rowsAffected > 0)
                    {
                        result.AddedCount++;
                    }
                    else
                    {
                        result.DuplicateCount++;
                    }
                }
                catch (Exception ex)
                {
                    result.ErrorCount++;
                    if (result.Errors.Count < 10)
                    {
                        result.Errors.Add($"Code '{code}': {ex.Message}");
                    }
                }
                processed++;
                progressCallback?.Invoke(processed, codesToAdd.Count);
            }

            transaction.Commit();
            result.Success = true;
            result.Message = $"Hoàn tất. Thêm mới: {result.AddedCount}, Trùng: {result.DuplicateCount}, Lỗi: {result.ErrorCount}";
        }
        catch (Exception ex)
        {
            try { transaction.Rollback(); } catch { /* ignore */ }
            result.Success = false;
            result.Message = $"Lỗi transaction: {ex.Message}";
        }

        return result;
    }

    // ---------- UpdateCodeStatus / UpdatePrintStatus ----------

    /// <summary>
    /// Cập nhật trạng thái mã trong pool: 0 = chưa dùng, 1 = đã dùng, -1 = lỗi.
    /// <para>Ưu tiên <paramref name="poolCode"/>; nếu không có thì dùng <paramref name="id"/>.</para>
    /// <list type="bullet">
    /// <item>newStatus=1: ghi batchID (mặc định <c>BATCH_yyyyMMddHHmmss</c>) + usedDatetime.</item>
    /// <item>newStatus=0: xóa batchID và usedDatetime.</item>
    /// <item>newStatus=-1: KHÔNG thay đổi batchID/usedDatetime.</item>
    /// </list>
    /// </summary>
    public DataPoolResult UpdateCodeStatus(string poolName, string? poolCode, long? id, int newStatus, string? batchID = null)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            return new DataPoolResult(false, "Tên Pool không được trống.");
        }
        if (newStatus != 0 && newStatus != 1 && newStatus != -1)
        {
            return new DataPoolResult(false, "Status không hợp lệ. Chỉ chấp nhận: 0 (chưa dùng), 1 (đã dùng), -1 (lỗi).");
        }
        if (string.IsNullOrWhiteSpace(poolCode) && !id.HasValue)
        {
            return new DataPoolResult(false, "Phải cung cấp PoolCode hoặc ID.");
        }

        var poolPathResult = GetPoolPath(poolName);
        if (!poolPathResult.Success)
        {
            return new DataPoolResult(false, poolPathResult.Message);
        }
        string poolPath = poolPathResult.Data;
        if (!File.Exists(poolPath))
        {
            return new DataPoolResult(false, "Pool không tồn tại.");
        }

        using var con = new SqliteConnection($"Data Source={poolPath}");
        con.Open();
        ApplyPragmas(con);

        string sql;
        SqliteCommand cmd;

        if (!string.IsNullOrWhiteSpace(poolCode))
        {
            sql = "UPDATE Codes SET Status = @status";
            if (newStatus == 1)
            {
                string effectiveBatchID = string.IsNullOrWhiteSpace(batchID)
                    ? $"BATCH_{DateTime.Now:yyyyMMddHHmmss}"
                    : batchID;
                sql += ", PoolCodeUsedBatchID = @batchID, PoolCodeUsedDatetime = @usedDatetime";
                sql += " WHERE PoolCode = @code";
                cmd = new SqliteCommand(sql, con);
                cmd.Parameters.AddWithValue("@code", poolCode);
                cmd.Parameters.AddWithValue("@status", newStatus);
                cmd.Parameters.AddWithValue("@batchID", effectiveBatchID);
                cmd.Parameters.AddWithValue("@usedDatetime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            else
            {
                if (newStatus == 0)
                {
                    sql += ", PoolCodeUsedBatchID = '', PoolCodeUsedDatetime = ''";
                }
                sql += " WHERE PoolCode = @code";
                cmd = new SqliteCommand(sql, con);
                cmd.Parameters.AddWithValue("@code", poolCode);
                cmd.Parameters.AddWithValue("@status", newStatus);
            }
        }
        else
        {
            sql = "UPDATE Codes SET Status = @status";
            if (newStatus == 1)
            {
                string effectiveBatchID = string.IsNullOrWhiteSpace(batchID)
                    ? $"BATCH_{DateTime.Now:yyyyMMddHHmmss}"
                    : batchID;
                sql += ", PoolCodeUsedBatchID = @batchID, PoolCodeUsedDatetime = @usedDatetime";
                sql += " WHERE ID = @id";
                cmd = new SqliteCommand(sql, con);
                cmd.Parameters.AddWithValue("@id", id!.Value);
                cmd.Parameters.AddWithValue("@status", newStatus);
                cmd.Parameters.AddWithValue("@batchID", effectiveBatchID);
                cmd.Parameters.AddWithValue("@usedDatetime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            else
            {
                if (newStatus == 0)
                {
                    sql += ", PoolCodeUsedBatchID = '', PoolCodeUsedDatetime = ''";
                }
                sql += " WHERE ID = @id";
                cmd = new SqliteCommand(sql, con);
                cmd.Parameters.AddWithValue("@id", id!.Value);
                cmd.Parameters.AddWithValue("@status", newStatus);
            }
        }

        int rowsAffected = cmd.ExecuteNonQuery();
        if (rowsAffected > 0)
        {
            string statusName = newStatus == 0 ? "chưa dùng" : (newStatus == 1 ? "đã dùng" : "lỗi");
            return new DataPoolResult(true, $"Cập nhật thành công sang trạng thái '{statusName}'. {rowsAffected} dòng bị ảnh hưởng.");
        }
        return new DataPoolResult(false, "Không tìm thấy mã code phù hợp.");
    }

    /// <summary>
    /// Cập nhật cột <c>PrintStatus</c>: 0 = chưa in, 1 = đã in.
    /// <para>Ưu tiên <paramref name="poolCode"/>; nếu không có thì dùng <paramref name="id"/>.</para>
    /// </summary>
    public DataPoolResult UpdatePrintStatus(string poolName, string? poolCode, long? id, int newPrintStatus)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            return new DataPoolResult(false, "Tên Pool không được trống.");
        }
        if (newPrintStatus != 0 && newPrintStatus != 1)
        {
            return new DataPoolResult(false, "PrintStatus không hợp lệ. Chỉ chấp nhận: 0 (chưa in), 1 (đã in).");
        }
        if (string.IsNullOrWhiteSpace(poolCode) && !id.HasValue)
        {
            return new DataPoolResult(false, "Phải cung cấp PoolCode hoặc ID.");
        }

        var poolPathResult = GetPoolPath(poolName);
        if (!poolPathResult.Success)
        {
            return new DataPoolResult(false, poolPathResult.Message);
        }
        string poolPath = poolPathResult.Data;
        if (!File.Exists(poolPath))
        {
            return new DataPoolResult(false, "Pool không tồn tại.");
        }

        using var con = new SqliteConnection($"Data Source={poolPath}");
        con.Open();
        ApplyPragmas(con);

        SqliteCommand cmd;
        if (!string.IsNullOrWhiteSpace(poolCode))
        {
            cmd = new SqliteCommand("UPDATE Codes SET PrintStatus = @printStatus WHERE PoolCode = @code", con);
            cmd.Parameters.AddWithValue("@code", poolCode);
            cmd.Parameters.AddWithValue("@printStatus", newPrintStatus);
        }
        else
        {
            cmd = new SqliteCommand("UPDATE Codes SET PrintStatus = @printStatus WHERE ID = @id", con);
            cmd.Parameters.AddWithValue("@id", id!.Value);
            cmd.Parameters.AddWithValue("@printStatus", newPrintStatus);
        }

        int rowsAffected = cmd.ExecuteNonQuery();
        if (rowsAffected > 0)
        {
            string statusName = newPrintStatus == 0 ? "chưa in" : "đã in";
            return new DataPoolResult(true, $"Cập nhật PrintStatus='{statusName}'. {rowsAffected} dòng bị ảnh hưởng.");
        }
        return new DataPoolResult(false, "Không tìm thấy mã code phù hợp.");
    }

    // ---------- Reads ----------

    /// <summary>Lấy thông tin pool + counter (Total / Unused / Used / Error).</summary>
    public DataPoolResult<PoolInfoWithCount> GetPoolInfo(string poolName)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            return new DataPoolResult<PoolInfoWithCount>(false, "Tên Pool không được trống.", null);
        }
        var poolPathResult = GetPoolPath(poolName);
        if (!poolPathResult.Success)
        {
            return new DataPoolResult<PoolInfoWithCount>(false, poolPathResult.Message, null);
        }
        string poolPath = poolPathResult.Data;
        if (!File.Exists(poolPath))
        {
            return new DataPoolResult<PoolInfoWithCount>(false, "Pool không tồn tại.", null);
        }

        using var con = new SqliteConnection($"Data Source={poolPath}");
        con.Open();
        ApplyPragmas(con);

        using var poolCmd = new SqliteCommand(
            "SELECT ID, PoolName, PoolDescription, PoolCreateID, PoolNote, PoolCreatedBy, PoolCreateDatetime FROM Pool LIMIT 1",
            con);
        using var reader = poolCmd.ExecuteReader();
        if (!reader.Read())
        {
            return new DataPoolResult<PoolInfoWithCount>(false, "Không tìm thấy thông tin Pool.", null);
        }

        var info = new PoolInfoWithCount(
            id: reader.GetInt64(0),
            name: reader.GetString(1),
            description: reader.GetString(2),
            createID: reader.GetString(3),
            note: reader.GetString(4),
            createdBy: reader.GetString(5),
            createDatetime: reader.GetString(6));
        reader.Close();

        using var countCmd = new SqliteCommand(@"
            SELECT
                COUNT(*) as TotalCount,
                SUM(CASE WHEN Status = 0 THEN 1 ELSE 0 END) as UnusedCount,
                SUM(CASE WHEN Status = 1 THEN 1 ELSE 0 END) as UsedCount,
                SUM(CASE WHEN Status = -1 THEN 1 ELSE 0 END) as ErrorCount
            FROM Codes", con);
        using var countReader = countCmd.ExecuteReader();
        if (countReader.Read())
        {
            info.Count = new PoolInfoWithCount.CodeCount(
                total: countReader.IsDBNull(0) ? 0 : countReader.GetInt32(0),
                unused: countReader.IsDBNull(1) ? 0 : countReader.GetInt32(1),
                used: countReader.IsDBNull(2) ? 0 : countReader.GetInt32(2),
                error: countReader.IsDBNull(3) ? 0 : countReader.GetInt32(3));
        }

        return new DataPoolResult<PoolInfoWithCount>(true, "Success", info);
    }

    /// <summary>Lấy 1 mã code theo PoolCode hoặc ID. Ưu tiên PoolCode.</summary>
    public DataPoolResult<DataTable> GetPoolCode(string poolName, string? poolCode, long? id)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            return new DataPoolResult<DataTable>(false, "Tên Pool không được trống.", null);
        }
        if (string.IsNullOrWhiteSpace(poolCode) && !id.HasValue)
        {
            return new DataPoolResult<DataTable>(false, "Phải cung cấp PoolCode hoặc ID.", null);
        }
        var poolPathResult = GetPoolPath(poolName);
        if (!poolPathResult.Success)
        {
            return new DataPoolResult<DataTable>(false, poolPathResult.Message, null);
        }
        string poolPath = poolPathResult.Data;
        if (!File.Exists(poolPath))
        {
            return new DataPoolResult<DataTable>(false, "Pool không tồn tại.", null);
        }

        using var con = new SqliteConnection($"Data Source={poolPath}");
        con.Open();
        ApplyPragmas(con);

        const string columnList =
            "ID, PoolCode, Status, PoolCodeUsedBatchID, PoolCodeUsedDatetime, PoolCodeNote, PoolCodeCreateID, PoolCodeCreatedBy, PoolCodeCreateDatetime, PrintStatus";

        string sql;
        if (!string.IsNullOrWhiteSpace(poolCode))
        {
            sql = $"SELECT {columnList} FROM Codes WHERE PoolCode = @code";
        }
        else
        {
            sql = $"SELECT {columnList} FROM Codes WHERE ID = @id";
        }

        using var cmd = new SqliteCommand(sql, con);
        if (!string.IsNullOrWhiteSpace(poolCode))
        {
            cmd.Parameters.AddWithValue("@code", poolCode);
        }
        else
        {
            cmd.Parameters.AddWithValue("@id", id!.Value);
        }

        var dt = new DataTable();
        using var reader = cmd.ExecuteReader();
        dt.Load(reader);

        if (dt.Rows.Count == 0)
        {
            return new DataPoolResult<DataTable>(false, "Không tìm thấy mã code.", dt);
        }
        return new DataPoolResult<DataTable>(true, "Success", dt);
    }

    /// <summary>Lấy danh sách mã code có phân trang (mặc định 100/trang) + filter.</summary>
    public DataPoolResult<PoolCodePageResult> GetPoolCodesPaginated(
        string poolName,
        int pageIndex = 1,
        int pageSize = 100,
        int? status = null,
        string? batchID = null,
        string? createID = null,
        string? createdBy = null,
        DateTime? fromCreateDate = null,
        DateTime? toCreateDate = null,
        DateTime? fromUsedDate = null,
        DateTime? toUsedDate = null)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            return new DataPoolResult<PoolCodePageResult>(false, "Tên Pool không được trống.", null);
        }
        if (pageIndex < 1) pageIndex = 1;
        if (pageSize < 1) pageSize = 100;

        var poolPathResult = GetPoolPath(poolName);
        if (!poolPathResult.Success)
        {
            return new DataPoolResult<PoolCodePageResult>(false, poolPathResult.Message, null);
        }
        string poolPath = poolPathResult.Data;
        if (!File.Exists(poolPath))
        {
            return new DataPoolResult<PoolCodePageResult>(false, "Pool không tồn tại.", null);
        }

        using var con = new SqliteConnection($"Data Source={poolPath}");
        con.Open();
        ApplyPragmas(con);

        var whereClauses = new List<string>();
        var parameters = new List<SqliteParameter>();

        if (status.HasValue)
        {
            whereClauses.Add("Status = @status");
            parameters.Add(new SqliteParameter("@status", status.Value));
        }
        if (!string.IsNullOrWhiteSpace(batchID))
        {
            whereClauses.Add("PoolCodeUsedBatchID = @batchID");
            parameters.Add(new SqliteParameter("@batchID", batchID));
        }
        if (!string.IsNullOrWhiteSpace(createID))
        {
            whereClauses.Add("PoolCodeCreateID = @createID");
            parameters.Add(new SqliteParameter("@createID", createID));
        }
        if (!string.IsNullOrWhiteSpace(createdBy))
        {
            whereClauses.Add("PoolCodeCreatedBy = @createdBy");
            parameters.Add(new SqliteParameter("@createdBy", createdBy));
        }
        if (fromCreateDate.HasValue)
        {
            whereClauses.Add("PoolCodeCreateDatetime >= @fromCreateDate");
            parameters.Add(new SqliteParameter("@fromCreateDate", fromCreateDate.Value.ToString("yyyy-MM-dd HH:mm:ss")));
        }
        if (toCreateDate.HasValue)
        {
            whereClauses.Add("PoolCodeCreateDatetime <= @toCreateDate");
            parameters.Add(new SqliteParameter("@toCreateDate", toCreateDate.Value.ToString("yyyy-MM-dd HH:mm:ss")));
        }
        if (fromUsedDate.HasValue)
        {
            whereClauses.Add("PoolCodeUsedDatetime >= @fromUsedDate");
            parameters.Add(new SqliteParameter("@fromUsedDate", fromUsedDate.Value.ToString("yyyy-MM-dd HH:mm:ss")));
        }
        if (toUsedDate.HasValue)
        {
            whereClauses.Add("PoolCodeUsedDatetime <= @toUsedDate");
            parameters.Add(new SqliteParameter("@toUsedDate", toUsedDate.Value.ToString("yyyy-MM-dd HH:mm:ss")));
        }

        string whereClause = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

        using var countCmd = new SqliteCommand($"SELECT COUNT(*) FROM Codes {whereClause}", con);
        foreach (var p in parameters) countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        int totalCount = Convert.ToInt32(countCmd.ExecuteScalar());

        int offset = (pageIndex - 1) * pageSize;

        const string columnList =
            "ID, PoolCode, Status, PoolCodeUsedBatchID, PoolCodeUsedDatetime, PoolCodeNote, PoolCodeCreateID, PoolCodeCreatedBy, PoolCodeCreateDatetime, PrintStatus";

        string sql = $@"SELECT {columnList}
                        FROM Codes {whereClause}
                        ORDER BY ID
                        LIMIT @limit OFFSET @offset";

        using var cmd = new SqliteCommand(sql, con);
        foreach (var p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        cmd.Parameters.AddWithValue("@limit", pageSize);
        cmd.Parameters.AddWithValue("@offset", offset);

        var dt = new DataTable();
        using var reader = cmd.ExecuteReader();
        dt.Load(reader);

        return new DataPoolResult<PoolCodePageResult>(true, "Success",
            new PoolCodePageResult(dt, totalCount, pageIndex, pageSize));
    }

    /// <summary>Lấy tổng số code + số đã dùng (Status=1).</summary>
    public DataPoolResult<CodeCount> GetCodeCounts(string poolName)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            return new DataPoolResult<CodeCount>(false, "Tên Pool không được trống.", null);
        }
        var poolPathResult = GetPoolPath(poolName);
        if (!poolPathResult.Success)
        {
            return new DataPoolResult<CodeCount>(false, poolPathResult.Message, null);
        }
        string poolPath = poolPathResult.Data;
        if (!File.Exists(poolPath))
        {
            return new DataPoolResult<CodeCount>(false, "Pool không tồn tại.", null);
        }

        using var con = new SqliteConnection($"Data Source={poolPath}");
        con.Open();
        ApplyPragmas(con);

        using var cmd = new SqliteCommand(@"
            SELECT
                COUNT(*) as TotalCount,
                SUM(CASE WHEN Status = 1 THEN 1 ELSE 0 END) as UsedCount
            FROM Codes", con);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return new DataPoolResult<CodeCount>(false, "Không thể đếm mã code.", null);
        }
        var count = new CodeCount(
            total: reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            used: reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
        return new DataPoolResult<CodeCount>(true, "Success", count);
    }

    /// <summary>Lấy toàn bộ code theo trạng thái. Nếu <paramref name="status"/> null → lấy tất cả.</summary>
    public DataPoolResult<DataTable> GetCodesByStatus(string poolName, int? status = null)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            return new DataPoolResult<DataTable>(false, "Tên Pool không được trống.", null);
        }
        var poolPathResult = GetPoolPath(poolName);
        if (!poolPathResult.Success)
        {
            return new DataPoolResult<DataTable>(false, poolPathResult.Message, null);
        }
        string poolPath = poolPathResult.Data;
        if (!File.Exists(poolPath))
        {
            return new DataPoolResult<DataTable>(false, "Pool không tồn tại.", null);
        }

        using var con = new SqliteConnection($"Data Source={poolPath}");
        con.Open();
        ApplyPragmas(con);

        const string columnList =
            "ID, PoolCode, Status, PoolCodeUsedBatchID, PoolCodeUsedDatetime, PoolCodeNote, PoolCodeCreateID, PoolCodeCreatedBy, PoolCodeCreateDatetime, PrintStatus";

        string sql = $"SELECT {columnList} FROM Codes";
        if (status.HasValue)
        {
            sql += " WHERE Status = @status";
        }
        sql += " ORDER BY ID";

        using var cmd = new SqliteCommand(sql, con);
        if (status.HasValue)
        {
            cmd.Parameters.AddWithValue("@status", status.Value);
        }

        var dt = new DataTable();
        using var reader = cmd.ExecuteReader();
        dt.Load(reader);

        if (dt.Rows.Count > 0)
        {
            dt.Columns.Add("StatusName", typeof(string));
            foreach (DataRow row in dt.Rows)
            {
                int st = Convert.ToInt32(row["Status"]);
                row["StatusName"] = st == 0 ? "Chưa dùng" : (st == 1 ? "Đã dùng" : "Lỗi");
            }
        }

        return new DataPoolResult<DataTable>(true, $"Tìm thấy {dt.Rows.Count} mã code.", dt);
    }

    /// <summary>
    /// Lấy danh sách mã đã in (<c>PrintStatus=1</c>) nhưng có <c>Status != excludeStatus</c> — dùng cho luồng in lại.
    /// <para>Mặc định <paramref name="excludeStatus"/> = 0 → lấy mọi mã đã in nhưng chưa dùng.</para>
    /// <para>KHÔNG thay đổi <c>PrintStatus</c> khi lấy.</para>
    /// </summary>
    public DataPoolResult<PoolCodePageResult> GetCodesForReprint(
        string poolName,
        int excludeStatus = 0,
        int pageIndex = 1,
        int pageSize = 100)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            return new DataPoolResult<PoolCodePageResult>(false, "Tên Pool không được trống.", null);
        }
        if (pageIndex < 1) pageIndex = 1;
        if (pageSize < 1) pageSize = 100;

        var poolPathResult = GetPoolPath(poolName);
        if (!poolPathResult.Success)
        {
            return new DataPoolResult<PoolCodePageResult>(false, poolPathResult.Message, null);
        }
        string poolPath = poolPathResult.Data;
        if (!File.Exists(poolPath))
        {
            return new DataPoolResult<PoolCodePageResult>(false, "Pool không tồn tại.", null);
        }

        using var con = new SqliteConnection($"Data Source={poolPath}");
        con.Open();
        ApplyPragmas(con);

        const string columnList =
            "ID, PoolCode, Status, PoolCodeUsedBatchID, PoolCodeUsedDatetime, PoolCodeNote, PoolCodeCreateID, PoolCodeCreatedBy, PoolCodeCreateDatetime, PrintStatus";

        const string whereClause = "WHERE PrintStatus = 1 AND Status != @excludeStatus";

        using var countCmd = new SqliteCommand($"SELECT COUNT(*) FROM Codes {whereClause}", con);
        countCmd.Parameters.AddWithValue("@excludeStatus", excludeStatus);
        int totalCount = Convert.ToInt32(countCmd.ExecuteScalar());

        int offset = (pageIndex - 1) * pageSize;

        string sql = $@"SELECT {columnList}
                        FROM Codes {whereClause}
                        ORDER BY ID
                        LIMIT @limit OFFSET @offset";

        using var cmd = new SqliteCommand(sql, con);
        cmd.Parameters.AddWithValue("@excludeStatus", excludeStatus);
        cmd.Parameters.AddWithValue("@limit", pageSize);
        cmd.Parameters.AddWithValue("@offset", offset);

        var dt = new DataTable();
        using var reader = cmd.ExecuteReader();
        dt.Load(reader);

        return new DataPoolResult<PoolCodePageResult>(true, "Success",
            new PoolCodePageResult(dt, totalCount, pageIndex, pageSize));
    }

    /// <summary>Lấy danh sách tất cả Pool trong thư mục, có phân trang (mặc định 100).</summary>
    public DataPoolResult<PoolListResult> GetPoolsPaginated(int pageIndex = 1, int pageSize = 100)
    {
        if (pageIndex < 1) pageIndex = 1;
        if (pageSize < 1) pageSize = 100;

        if (!Directory.Exists(_databasePath))
        {
            return new DataPoolResult<PoolListResult>(true, "Thư mục không tồn tại.",
                new PoolListResult(new List<PoolInfoBasic>(), 0, pageIndex, pageSize));
        }

        var files = Directory.GetFiles(_databasePath, "*.db");
        var poolInfos = new List<PoolInfoBasic>();
        var invalidFiles = new List<string>();

        foreach (var file in files)
        {
            string fileName = Path.GetFileNameWithoutExtension(file);
            try
            {
                using var con = new SqliteConnection($"Data Source={file}");
                con.Open();
                ApplyPragmas(con);

                using var cmd = new SqliteCommand(
                    "SELECT ID, PoolName, PoolDescription, PoolCreateID, PoolNote, PoolCreatedBy, PoolCreateDatetime FROM Pool LIMIT 1",
                    con);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    poolInfos.Add(new PoolInfoBasic(
                        id: reader.GetInt64(0),
                        name: reader.GetString(1),
                        description: reader.GetString(2),
                        createID: reader.GetString(3),
                        note: reader.GetString(4),
                        createdBy: reader.GetString(5),
                        createDatetime: reader.GetString(6),
                        filePath: file));
                }
                else
                {
                    invalidFiles.Add($"{fileName}: bảng Pool rỗng.");
                }
            }
            catch (Exception ex)
            {
                invalidFiles.Add($"{fileName}: {ex.Message}");
            }
        }

        int totalCount = poolInfos.Count;
        if (totalCount == 0)
        {
            string msg = invalidFiles.Count > 0
                ? "Không có Pool hợp lệ. File lỗi: " + string.Join("; ", invalidFiles)
                : "Không có Pool nào.";
            return new DataPoolResult<PoolListResult>(true, msg,
                new PoolListResult(new List<PoolInfoBasic>(), 0, pageIndex, pageSize));
        }

        int offset = (pageIndex - 1) * pageSize;
        var pagedList = poolInfos.OrderBy(p => p.ID).Skip(offset).Take(pageSize).ToList();

        string resultMessage = invalidFiles.Count > 0
            ? "Success (bỏ qua file lỗi: " + string.Join("; ", invalidFiles) + ")"
            : "Success";
        return new DataPoolResult<PoolListResult>(true, resultMessage,
            new PoolListResult(pagedList, totalCount, pageIndex, pageSize));
    }
}
