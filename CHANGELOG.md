# Changelog

## Unreleased

### Changed
- `RDCZP` now runs through `RunAsync(CancellationToken)` as one awaited connection, receive, and reconnect loop. The previous fire-and-forget `Connect()` flow has been removed.
- TCP connectivity is tested directly; ICMP ping is no longer required before connecting.
- Received UTF-8 data is buffered across TCP packets and emitted as complete frames. `\r`, `\n`, and `\r\n` are accepted as delimiters, delimiters are excluded, and empty frames are ignored.
- `SendAsync` accepts an optional `CancellationToken` and continues until the complete UTF-8 payload has been sent.
- `Disconnect()` cancels the active run and closes its socket without starting another reconnect attempt.

### Added
- `ReceivedDataProcessor` (replaces `CameraRDWorker`): dedicated background service that processes received Datamatrix codes on a **single pinned worker thread** with **drop-if-busy** semantics.
  - Incoming codes received from the camera callback thread are enqueued only when the worker is idle and no code is pending.
  - If the worker is busy (`_isProcessing = true`) or a code is already pending, new incoming codes are silently dropped.
  - A `ManualResetEventSlim` signals the worker thread when work is available; no busy-waiting.
  - Worker thread is explicitly created (`Thread`, not `Task.Run`) so it has a stable `ManagedThreadId` for logging and is isolated from the ThreadPool.
  - Clean shutdown: `_stopRequested` flag stops the worker loop, then `Join()` ensures the thread exits before the host stops.

### Integration notes
- Call and await `RDCZP.RunAsync(hostCancellationToken)` instead of calling `Connect()`.
- Subscribe to `ClientCallback` before starting `RunAsync`; `Received` callbacks now contain exactly one complete frame.
- Register one `RDCZP` instance for each physical camera. The backend currently registers one singleton instance.

### Verification
- Build with `dotnet build MasanSolution.slnx`.
- Run isolated TCP loopback tests with `dotnet test Tests/RDCZP.Tests/RDCZP.Tests.csproj`.
