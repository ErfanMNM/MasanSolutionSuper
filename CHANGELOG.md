# Changelog

## Unreleased

### Added
- **Port DataPool + ProductionOrder modules** từ `CProject` sang `MasanCZCodeBackend` (.NET 10, async-ready).
  - `Configuration/StorageOptions.cs`: POCO bind section `"Storage"` (`DataPoolPath`, `PoDatabasePath`, `BusyTimeoutMs`) từ `appsettings.json`.
  - `DataPool/PoolModels.cs`: `PoolInfo`, `PoolCodeInfo` (giữ tên field `PoolCodeStatus` như file gốc, thêm cột mới `PrintStatus`), `PoolInfoWithCount`, `CodeCount`, `PoolCodePageResult`, `PoolListResult`, `PoolInfoBasic`. `ID` đổi từ `double` → `long` để tránh mất precision với `INTEGER` lớn.
  - `DataPool/DataPoolResult.cs`: `DataPoolResult`, `DataPoolResult<T>`, `DataPoolResultString`.
  - `DataPool/DataPoolAddCodesResult.cs`: result thêm code (Total/Added/Duplicate/Error + list lỗi).
  - `DataPool/DataPoolModule.cs`: port 12 method gốc (`GetPoolPath`, `CreatePool`, `AddCodes`, `UpdateCodeStatus`, `GetPoolInfo`, `GetPoolCode`, `GetPoolCodesPaginated`, `GetCodeCounts`, `GetCodesByStatus`, `GetPoolsPaginated`) + 2 method mới (`UpdatePrintStatus`, `GetCodesForReprint`). Schema Codes thêm cột `PrintStatus` + 2 index (`IDX_Codes_PrintStatus`, `IDX_Codes_Status_PrintStatus`).
  - `ProductionOrder/POModule.cs`: gộp `POInfo` / `Product_Counter` / `CartonInfo` / `ProductInfo` + class `PODatabase` (`InitializeDatabase`, `CreateProductionOrder` trả tuple `(bool, string, int)` y hệt file gốc).
  - `appsettings.json` + `appsettings.Development.json`: section `Storage` với đường dẫn mặc định khớp file gốc (`C:\CProject\DataPool`, `C:\CProject\PoDatabase`).
  - `Microsoft.Data.Sqlite` 10.0.12 được thêm vào `MasanCZCodeBackend.csproj`.
  - DI trong `Program.cs`: `Configure<StorageOptions>` + `AddSingleton<DataPoolModule>()` + `AddSingleton<PODatabase>()`.

### Concurrency
- **DataPool**: `PRAGMA journal_mode=WAL` + `busy_timeout=5000` + `synchronous=NORMAL`. Không dùng `SemaphoreSlim` per-pool — dựa vào WAL + connection pooling mặc định của `Microsoft.Data.Sqlite`.
- **PODatabase**: cùng pragmas như DataPool. `CreateProductionOrder` dùng `SemaphoreSlim(1,1)` với `Wait(0)` — nếu đang bận thì fail ngay với message `"PO database đang bận, thử lại sau"` (không block caller). Read operations không lock (nhờ WAL).

### Integration notes
- Inject `IOptions<StorageOptions>` vào constructor của module để lấy đường dẫn + busy_timeout (không hard-code path nữa).
- Gọi `PODatabase.InitializeDatabase()` một lần khi app khởi động để đảm bảo bảng + WAL đã sẵn sàng (TODO: thêm hosted service nếu cần).
- `DataPoolModule.UpdateCodeStatus(...)` ưu tiên `poolCode`; nếu rỗng thì fallback `id`. Status hợp lệ: `0` (chưa dùng), `1` (đã dùng), `-1` (lỗi).
- `DataPoolModule.UpdatePrintStatus(...)` mirror pattern trên cho cột `PrintStatus` (chỉ `0|1`).
- `DataPoolModule.GetCodesForReprint(poolName, excludeStatus=0, pageIndex, pageSize)` — lấy danh sách mã đã in nhưng chưa dùng (Status != 0), KHÔNG đổi `PrintStatus`.

### Changed
- `RDCZP` now runs through `RunAsync(CancellationToken)` as one awaited connection, receive, and reconnect loop. The previous fire-and-forget `Connect()` flow has been removed.
- TCP connectivity is tested directly; ICMP ping is no longer required before connecting.
- Received UTF-8 data is buffered across TCP packets and emitted as complete frames. `\r`, `\n`, and `\r\n` are accepted as delimiters, delimiters are excluded, and empty frames are ignored.
- `SendAsync` accepts an optional `CancellationToken` and continues until the complete UTF-8 payload has been sent.
- `Disconnect()` cancels the active run and closes its socket without starting another reconnect attempt.

### Changed (RDCZP processing pipeline)
- `ReceivedDataProcessor` replaces `CameraRDWorker`: dedicated background service that processes received Datamatrix codes on a **single pinned worker thread** with **drop-if-busy** semantics.
  - Incoming codes received from the camera callback thread are enqueued only when the worker is idle and no code is pending.
  - If the worker is busy (`_isProcessing = true`) or a code is already pending, new incoming codes are silently dropped.
  - A `ManualResetEventSlim` signals the worker thread when work is available; no busy-waiting.
  - Worker thread is explicitly created (`Thread`, not `Task.Run`) so it has a stable `ManagedThreadId` for logging and is isolated from the ThreadPool.
  - Clean shutdown: `_stopRequested` flag stops the worker loop, then `Join()` ensures the thread exits before the host stops.

### Integration notes (RDCZP)
- Call and await `RDCZP.RunAsync(hostCancellationToken)` instead of calling `Connect()`.
- Subscribe to `ClientCallback` before starting `RunAsync`; `Received` callbacks now contain exactly one complete frame.
- Register one `RDCZP` instance for each physical camera. The backend currently registers one singleton instance.

### Verification
- Build with `dotnet build MasanSolution.slnx`.
- Run isolated TCP loopback tests with `dotnet test Tests/RDCZP.Tests/RDCZP.Tests.csproj`.
