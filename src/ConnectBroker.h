#pragma once
#include <winsock2.h>
#include <windows.h>
#include <string>

// The original destination crosses an authenticated pipe before any WFP redirect.
struct ConnectRequest { DWORD version = 1; DWORD address = 0; USHORT port = 0; USHORT reserved = 0; };
struct ConnectReply { DWORD error = ERROR_ACCESS_DENIED; USHORT port = 0; USHORT reserved = 0; };
inline std::wstring ConnectPipe(const std::wstring& job) {
    return L"\\\\.\\pipe\\RegionLauncher.Connect." + job.substr(job.find_last_of(L'.') + 1);
}
bool StartConnectBroker(const std::wstring& pipe, HANDLE job, USHORT proxyPort);
void StopConnectBroker();
bool AttachConnectHooks();
