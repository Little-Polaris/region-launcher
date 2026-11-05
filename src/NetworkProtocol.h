#pragma once
#include <windows.h>
#include <stdint.h>

constexpr WCHAR ServiceName[] = L"RegionLauncherNetwork";
constexpr WCHAR PipeName[] = L"\\\\.\\pipe\\RegionLauncher.Network.v1";
constexpr DWORD ProtocolVersion = 1;
struct NetworkRequest {
    DWORD version = ProtocolVersion;
    DWORD port = 0;
    uint64_t job = 0, ready = 0, stop = 0;
    DWORD proxyLoopback = 0;
    DWORD dns = 0; // Network-byte-order IPv4, zero disables the fixture's DNS relay.
};
struct NetworkReply { DWORD error = 0, version = ProtocolVersion; uint64_t worker = 0; };
static_assert(sizeof(NetworkRequest) == 40 && sizeof(NetworkReply) == 16);

// Bounded I/O also handles partial or stalled clients without blocking service shutdown.
inline bool PipeIO(HANDLE pipe, void* data, DWORD size, bool write, HANDLE stop = nullptr) {
    OVERLAPPED ov = {}; ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!ov.hEvent) return false;
    DWORD done = 0;
    BOOL ok = write ? WriteFile(pipe, data, size, &done, &ov) : ReadFile(pipe, data, size, &done, &ov);
    DWORD err = ok ? ERROR_SUCCESS : GetLastError();
    if (!ok && err == ERROR_IO_PENDING) {
        HANDLE waits[] = {ov.hEvent, stop};
        if (WaitForMultipleObjects(stop ? 2 : 1, waits, FALSE, 5000) == WAIT_OBJECT_0) {
            ok = GetOverlappedResult(pipe, &ov, &done, FALSE); err = ok ? ERROR_SUCCESS : GetLastError();
        } else {
            CancelIoEx(pipe, &ov); GetOverlappedResult(pipe, &ov, &done, TRUE); err = ERROR_TIMEOUT;
        }
    }
    CloseHandle(ov.hEvent);
    if (!ok || done != size) { SetLastError(err ? err : ERROR_INVALID_DATA); return false; }
    return true;
}
