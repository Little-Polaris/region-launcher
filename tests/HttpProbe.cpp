#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winhttp.h>
#include <stdio.h>
#include <string>

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 2;
    URL_COMPONENTS url = {}; url.dwStructSize = sizeof(url);
    url.dwHostNameLength = url.dwUrlPathLength = url.dwExtraInfoLength = DWORD(-1);
    if (!WinHttpCrackUrl(argv[1], 0, 0, &url)) return 3;
    std::wstring host(url.lpszHostName, url.dwHostNameLength), path(url.lpszUrlPath, url.dwUrlPathLength);
    if (url.dwExtraInfoLength) path.append(url.lpszExtraInfo, url.dwExtraInfoLength);
    HINTERNET session = WinHttpOpen(L"RegionLauncher validation", WINHTTP_ACCESS_TYPE_NO_PROXY, nullptr, nullptr, 0);
    if (!session) return 4;
    WinHttpSetTimeouts(session, 10000, 10000, 10000, 10000);
    HINTERNET connection = WinHttpConnect(session, host.c_str(), url.nPort, 0);
    HINTERNET request = connection ? WinHttpOpenRequest(connection, L"GET", path.c_str(), nullptr, nullptr, nullptr,
        url.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0) : nullptr;
    bool ok = request && WinHttpSendRequest(request, nullptr, 0, nullptr, 0, 0, 0) && WinHttpReceiveResponse(request, nullptr);
    DWORD status = 0, size = sizeof(status);
    if (ok) ok = WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, nullptr, &status, &size, nullptr) && status == 200;
    DWORD error = ok ? 0 : GetLastError();
    printf("BITS=%u HTTP=%lu ERROR=%lu\n", unsigned(sizeof(void*)*8), status, error);
    if (ok) {
        char data[4097]; DWORD read = 0;
        while (WinHttpReadData(request, data, 4096, &read) && read) { fwrite(data, 1, read, stdout); }
    }
    if (request) WinHttpCloseHandle(request);
    if (connection) WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);
    return ok ? 0 : 1;
}
