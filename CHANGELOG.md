# Changelog

## Unreleased

### Changed
- `RDCZP` now runs through `RunAsync(CancellationToken)` as one awaited connection, receive, and reconnect loop. The previous fire-and-forget `Connect()` flow has been removed.
- TCP connectivity is tested directly; ICMP ping is no longer required before connecting.
- Received UTF-8 data is buffered across TCP packets and emitted as complete frames. `\r`, `\n`, and `\r\n` are accepted as delimiters, delimiters are excluded, and empty frames are ignored.
- `SendAsync` accepts an optional `CancellationToken` and continues until the complete UTF-8 payload has been sent.
- `Disconnect()` cancels the active run and closes its socket without starting another reconnect attempt.
- `CameraRDWorker` owns the client lifetime through the Generic Host. The temporary camera endpoint is fixed at `127.0.0.1:9000` until application-wide configuration is introduced.

### Integration notes
- Call and await `RDCZP.RunAsync(hostCancellationToken)` instead of calling `Connect()`.
- Subscribe to `ClientCallback` before starting `RunAsync`; `Received` callbacks now contain exactly one complete frame.
- Register one `RDCZP` instance for each physical camera. The backend currently registers one singleton instance.

### Verification
- Build with `dotnet build MasanSolution.slnx`.
- Run isolated TCP loopback tests with `dotnet test Tests/RDCZP.Tests/RDCZP.Tests.csproj`.
