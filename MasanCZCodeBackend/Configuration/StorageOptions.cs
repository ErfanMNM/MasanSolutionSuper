namespace MasanCZCodeBackend.Configuration;

/// <summary>
/// Cấu hình đường dẫn lưu trữ cho DataPool và PODatabase.
/// Bind từ section "Storage" trong appsettings.json.
/// </summary>
public class StorageOptions
{
    /// <summary>
    /// Đường dẫn thư mục chứa các file *.db của các Pool.
    /// Mặc định khớp với file gốc CProject: C:\CProject\DataPool
    /// </summary>
    public string DataPoolPath { get; set; } = @"C:\CProject\DataPool";

    /// <summary>
    /// Đường dẫn thư mục chứa PO_List.db (và các file runtime yyyy-MM/... sau này).
    /// Mặc định khớp với file gốc CProject: C:/CProject/PoDatabase
    /// </summary>
    public string PoDatabasePath { get; set; } = @"C:\CProject\PoDatabase";

    /// <summary>
    /// Thời gian busy_timeout (ms) cho cả DataPool và PODatabase.
    /// </summary>
    public int BusyTimeoutMs { get; set; } = 5000;
}
