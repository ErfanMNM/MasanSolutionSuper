namespace MasanCZCodeBackend.DataPool;

/// <summary>
/// Kết quả của <c>DataPoolModule.AddCodes</c>: thống kê thêm mới / trùng / lỗi + danh sách lỗi chi tiết.
/// </summary>
public class DataPoolAddCodesResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public int AddedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ErrorCount { get; set; }
    public List<string> Errors { get; set; } = new();

    public DataPoolAddCodesResult() { }

    public DataPoolAddCodesResult(bool success, string message)
    {
        Success = success;
        Message = message;
    }
}
