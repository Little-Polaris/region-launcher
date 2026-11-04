#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <stdio.h>
#include <string>

int wmain(int argc, wchar_t** argv) {
    WCHAR locale[LOCALE_NAME_MAX_LENGTH] = {}, region[32] = {};
    GetUserDefaultLocaleName(locale, _countof(locale)); GetUserDefaultGeoName(region, _countof(region));
    DYNAMIC_TIME_ZONE_INFORMATION tz = {}; DWORD season = GetDynamicTimeZoneInformation(&tz);
    SYSTEMTIME utc = {}, local = {}; GetSystemTime(&utc); GetLocalTime(&local);
    wprintf(L"PID=%lu locale=%s region=%s geo=%ld timezone=%s dstDisabled=%u season=%lu localHour=%u utcHour=%u\n",
        GetCurrentProcessId(), locale, region, GetUserGeoID(GEOCLASS_NATION), tz.TimeZoneKeyName,
        tz.DynamicDaylightTimeDisabled, season, local.wHour, utc.wHour);
    SYSTEMTIME winter = {2026, 1, 0, 15, 12, 0, 0, 0}, summer = {2026, 7, 0, 15, 12, 0, 0, 0};
    SYSTEMTIME wlocal = {}, slocal = {}; SystemTimeToTzSpecificLocalTimeEx(&tz, &winter, &wlocal); SystemTimeToTzSpecificLocalTimeEx(&tz, &summer, &slocal);
    wprintf(L"winterHour=%u summerHour=%u\n", wlocal.wHour, slocal.wHour);
    if (argc > 1 && !wcscmp(argv[1], L"--child")) return 0;
    if (argc > 1 && !wcscmp(argv[1], L"--network") && argc >= 4) {
        WSADATA data; WSAStartup(MAKEWORD(2, 2), &data);
        SOCKET sock = socket(AF_INET, SOCK_STREAM, 0); DWORD timeout = 3000;
        setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));
        sockaddr_in target = {}; target.sin_family = AF_INET; target.sin_port = htons((u_short)_wtoi(argv[3])); InetPtonW(AF_INET, argv[2], &target.sin_addr);
        int status = connect(sock, (sockaddr*)&target, sizeof(target));
        if (status == 0) { send(sock, "probe", 5, 0); char reply[256] = {}; int n = recv(sock, reply, sizeof(reply)-1, 0); if (n > 0) printf("NETWORK=%s\n", reply); else printf("NETWORK_ERROR=%d\n", WSAGetLastError()); }
        else printf("CONNECT_ERROR=%d\n", WSAGetLastError());
        closesocket(sock); WSACleanup(); return status == 0 ? 0 : 3;
    }
    WCHAR self[32768]; GetModuleFileNameW(nullptr, self, _countof(self));
    std::wstring cmd = L"\"" + std::wstring(self) + L"\" --child";
    STARTUPINFOW si = {sizeof(si)}; PROCESS_INFORMATION pi = {};
    if (!CreateProcessW(self, cmd.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi)) { printf("CHILD_ERROR=%lu\n", GetLastError()); return 1; }
    WaitForSingleObject(pi.hProcess, 5000); CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    if (GetEnvironmentVariableW(L"REGION_LAUNCHER_LOCALE", nullptr, 0)) {
        WCHAR buffer[32768]; if (GetEnvironmentVariableW(L"REGION_PROBE_FILE", buffer, _countof(buffer))) {
            HANDLE f = CreateFileW(buffer, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (f == INVALID_HANDLE_VALUE) { printf("FILE_ERROR=%lu\n", GetLastError()); return 1; }
            DWORD written; WriteFile(f, "host file access verified", 25, &written, nullptr); CloseHandle(f); printf("FILE_OK\n");
        }
    }
    return 0;
}
