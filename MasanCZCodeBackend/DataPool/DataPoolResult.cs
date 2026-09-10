namespace MasanCZCodeBackend.DataPool;

/// <summary>
/// Kết quả chuẩn (không có payload) cho các method của <see cref="DataPoolModule"/>.
/// </summary>
public class DataPoolResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    public DataPoolResult() { }

    public DataPoolResult(bool success, string message)
    {
        Success = success;
        Message = message;
    }
}

/// <summary>
/// Kết quả generic cho các method trả về payload (DataTable, struct, ...).
/// </summary>
public class DataPoolResult<T> : DataPoolResult
{
    public T? Data { get; set; }

    public DataPoolResult() { }

    public DataPoolResult(bool success, string message, T? data) : base(success, message)
    {
        Data = data;
    }
}

/// <summary>Kết quả trả về kèm chuỗi (dùng cho <c>GetPoolPath</c>, <c>CreatePool</c>).</summary>
public class DataPoolResultString
{
    public bool Success { get; set; } = false;
    public string Message { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;

    public DataPoolResultString() { }

    public DataPoolResultString(bool success, string message, string data)
    {
        Success = success;
        Message = message;
        Data = data;
    }
}
