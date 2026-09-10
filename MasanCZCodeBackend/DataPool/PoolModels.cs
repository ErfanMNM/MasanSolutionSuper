using System.Data;

namespace MasanCZCodeBackend.DataPool;

/// <summary>
/// Thông tin một Pool — mirror cấu trúc bảng Pool trong file gốc CProject.
/// ID dùng <see cref="long"/> thay vì <c>double</c> của file gốc để tránh mất precision khi ID lớn.
/// </summary>
public class PoolInfo
{
    public long ID { get; set; }
    public string PoolName { get; set; } = string.Empty;
    public string PoolDescription { get; set; } = string.Empty;
    public string PoolCreateID { get; set; } = string.Empty;
    public string PoolNote { get; set; } = string.Empty;
    public string PoolCreatedBy { get; set; } = string.Empty;
    public string PoolCreateDatetime { get; set; } = string.Empty;

    /// <summary>
    /// Counter tổng hợp (Total / Used / Unused / Error). Tương đương <c>PoolInfo.PoolCount</c> trong file gốc.
    /// </summary>
    public class PoolCount
    {
        public int TotalCount { get; set; } = 0;
        public int UsedCount { get; set; } = 0;
        public int UnusedCount { get; set; } = 0;
        public int ErrorCount { get; set; } = 0;

        public PoolCount() { }

        public PoolCount(int total, int used, int unused, int error)
        {
            TotalCount = total;
            UsedCount = used;
            UnusedCount = unused;
            ErrorCount = error;
        }
    }

    public PoolInfo() { }

    public PoolInfo(long id, string name, string description, string createID, string note, string createdBy, string createDatetime)
    {
        ID = id;
        PoolName = name;
        PoolDescription = description;
        PoolCreateID = createID;
        PoolNote = note;
        PoolCreatedBy = createdBy;
        PoolCreateDatetime = createDatetime;
    }
}

/// <summary>
/// Thông tin một mã code trong Pool — mirror bảng Codes trong file gốc.
/// <para>
/// Lưu ý: tên field status là <c>PoolCodeStatus</c> (không phải <c>Status</c>) để khớp ngữ nghĩa file gốc.
/// </para>
/// </summary>
public class PoolCodeInfo
{
    public long ID { get; set; }
    public string PoolCode { get; set; } = string.Empty;
    public int PoolCodeStatus { get; set; } = 0;
    public string PoolCodeUsedBatchID { get; set; } = string.Empty;
    public string PoolCodeUsedDatetime { get; set; } = string.Empty;
    public string PoolCodeNote { get; set; } = string.Empty;
    public string PoolCodeCreateID { get; set; } = string.Empty;
    public string PoolCodeCreatedBy { get; set; } = string.Empty;
    public string PoolCodeCreateDatetime { get; set; } = string.Empty;

    /// <summary>0 = chưa in, 1 = đã in. Mặc định 0 (cột bổ sung so với file gốc).</summary>
    public int PrintStatus { get; set; } = 0;

    public PoolCodeInfo() { }

    public PoolCodeInfo(
        long id,
        string code,
        int status,
        string usedBatchID,
        string usedDatetime,
        string note,
        string createID,
        string createdBy,
        string createDatetime,
        int printStatus = 0)
    {
        ID = id;
        PoolCode = code;
        PoolCodeStatus = status;
        PoolCodeUsedBatchID = usedBatchID;
        PoolCodeUsedDatetime = usedDatetime;
        PoolCodeNote = note;
        PoolCodeCreateID = createID;
        PoolCodeCreatedBy = createdBy;
        PoolCodeCreateDatetime = createDatetime;
        PrintStatus = printStatus;
    }

    /// <summary>Static placeholder tương đương class lồng trong file gốc (giữ để tương thích).</summary>
    public static class GET
    {
    }
}

/// <summary>
/// Thông tin Pool kèm counter (Total / Unused / Used / Error) — dùng cho <c>GetPoolInfo</c>.
/// </summary>
public class PoolInfoWithCount
{
    public long ID { get; set; }
    public string PoolName { get; set; } = string.Empty;
    public string PoolDescription { get; set; } = string.Empty;
    public string PoolCreateID { get; set; } = string.Empty;
    public string PoolNote { get; set; } = string.Empty;
    public string PoolCreatedBy { get; set; } = string.Empty;
    public string PoolCreateDatetime { get; set; } = string.Empty;

    public CodeCount? Count { get; set; }

    public PoolInfoWithCount() { }

    public PoolInfoWithCount(long id, string name, string description, string createID, string note, string createdBy, string createDatetime)
    {
        ID = id;
        PoolName = name;
        PoolDescription = description;
        PoolCreateID = createID;
        PoolNote = note;
        PoolCreatedBy = createdBy;
        PoolCreateDatetime = createDatetime;
    }

    /// <summary>Counter của pool: Total / Unused / Used / Error.</summary>
    public class CodeCount
    {
        public int TotalCount { get; set; }
        public int UnusedCount { get; set; }
        public int UsedCount { get; set; }
        public int ErrorCount { get; set; }

        public CodeCount() { }

        public CodeCount(int total, int unused, int used, int error)
        {
            TotalCount = total;
            UnusedCount = unused;
            UsedCount = used;
            ErrorCount = error;
        }
    }
}

/// <summary>Counter chỉ gồm Total / Used — dùng cho <c>GetCodeCounts</c>.</summary>
public class CodeCount
{
    public int TotalCount { get; set; }
    public int UsedCount { get; set; }

    public CodeCount() { }

    public CodeCount(int total, int used)
    {
        TotalCount = total;
        UsedCount = used;
    }
}

/// <summary>Kết quả phân trang cho <c>GetPoolCodesPaginated</c>.</summary>
public class PoolCodePageResult
{
    public DataTable Data { get; set; } = new();
    public int TotalCount { get; set; }
    public int PageIndex { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public bool HasNextPage => PageIndex < TotalPages;
    public bool HasPrevPage => PageIndex > 1;

    public PoolCodePageResult() { }

    public PoolCodePageResult(DataTable data, int totalCount, int pageIndex, int pageSize)
    {
        Data = data;
        TotalCount = totalCount;
        PageIndex = pageIndex;
        PageSize = pageSize;
    }
}

/// <summary>Kết quả phân trang cho <c>GetPoolsPaginated</c>.</summary>
public class PoolListResult
{
    public List<PoolInfoBasic> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int PageIndex { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public bool HasNextPage => PageIndex < TotalPages;
    public bool HasPrevPage => PageIndex > 1;

    public PoolListResult() { }

    public PoolListResult(List<PoolInfoBasic> items, int totalCount, int pageIndex, int pageSize)
    {
        Items = items;
        TotalCount = totalCount;
        PageIndex = pageIndex;
        PageSize = pageSize;
    }
}

/// <summary>Thông tin cơ bản của một Pool dùng trong danh sách Pool (kèm đường dẫn file).</summary>
public class PoolInfoBasic
{
    public long ID { get; set; }
    public string PoolName { get; set; } = string.Empty;
    public string PoolDescription { get; set; } = string.Empty;
    public string PoolCreateID { get; set; } = string.Empty;
    public string PoolNote { get; set; } = string.Empty;
    public string PoolCreatedBy { get; set; } = string.Empty;
    public string PoolCreateDatetime { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;

    public PoolInfoBasic() { }

    public PoolInfoBasic(long id, string name, string description, string createID, string note, string createdBy, string createDatetime, string filePath)
    {
        ID = id;
        PoolName = name;
        PoolDescription = description;
        PoolCreateID = createID;
        PoolNote = note;
        PoolCreatedBy = createdBy;
        PoolCreateDatetime = createDatetime;
        FilePath = filePath;
    }
}
