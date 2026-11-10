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
    if (GetEnvironmentVariableW(L"REGION_PROBE_HOOK_PATH", nullptr, 0)) {
        WCHAR module[32768] = {};
        if (HMODULE hook = GetModuleHandleW(L"RegionHook.dll")) GetModuleFileNameW(hook, module, _countof(module));
        char utf8[131072] = {}; WideCharToMultiByte(CP_UTF8, 0, module, -1, utf8, sizeof(utf8), nullptr, nullptr);
        printf("HOOK_PATH=%s\n", utf8);
    }
    SYSTEMTIME winter = {2026, 1, 0, 15, 12, 0, 0, 0}, summer = {2026, 7, 0, 15, 12, 0, 0, 0};
    SYSTEMTIME wlocal = {}, slocal = {}; SystemTimeToTzSpecificLocalTimeEx(&tz, &winter, &wlocal); SystemTimeToTzSpecificLocalTimeEx(&tz, &summer, &slocal);
    wprintf(L"winterHour=%u summerHour=%u\n", wlocal.wHour, slocal.wHour);
    SYSTEMTIME springBefore = {2026, 3, 0, 8, 9, 59, 0, 0}, springAfter = {2026, 3, 0, 8, 10, 0, 0, 0};
    SYSTEMTIME fallBefore = {2026, 11, 0, 1, 8, 59, 0, 0}, fallAfter = {2026, 11, 0, 1, 9, 0, 0, 0};
    SYSTEMTIME a, b, c, d; SystemTimeToTzSpecificLocalTimeEx(&tz, &springBefore, &a); SystemTimeToTzSpecificLocalTimeEx(&tz, &springAfter, &b);
    SystemTimeToTzSpecificLocalTimeEx(&tz, &fallBefore, &c); SystemTimeToTzSpecificLocalTimeEx(&tz, &fallAfter, &d);
    wprintf(L"spring=%u:%02u>%u:%02u fall=%u:%02u>%u:%02u\n", a.wHour, a.wMinute, b.wHour, b.wMinute, c.wHour, c.wMinute, d.wHour, d.wMinute);
    if (argc > 1 && !wcscmp(argv[1], L"--child")) return 0;
    if (argc > 1 && !wcscmp(argv[1], L"--hold")) { fflush(stdout); Sleep(15000); return 0; }
    if (argc > 1 && (!wcscmp(argv[1], L"--network") || !wcscmp(argv[1], L"--udp")) && argc >= 4) {
        Sleep(1000);
        WSADATA data; WSAStartup(MAKEWORD(2, 2), &data);
        bool udp = !wcscmp(argv[1], L"--udp");
        SOCKET sock = socket(AF_INET, udp ? SOCK_DGRAM : SOCK_STREAM, 0); DWORD timeout = 3000;
        setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));
        sockaddr_in target = {}; target.sin_family = AF_INET; target.sin_port = htons((u_short)_wtoi(argv[3])); InetPtonW(AF_INET, argv[2], &target.sin_addr);
        int status = connect(sock, (sockaddr*)&target, sizeof(target));
        if (status == 0) { send(sock, "probe", 5, 0); char reply[256] = {}; int n = recv(sock, reply, sizeof(reply)-1, 0); if (n > 0) printf("NETWORK=%s\n", reply); else { printf("NETWORK_ERROR=%d\n", WSAGetLastError()); status = -1; } }
        else printf("CONNECT_ERROR=%d\n", WSAGetLastError());
        closesocket(sock); WSACleanup();
        if (argc >= 5) { int hold = _wtoi(argv[4]); if (hold > 0 && hold <= 5000) Sleep(hold); }
        return status == 0 ? 0 : 3;
    }
    WCHAR self[32768]; GetModuleFileNameW(nullptr, self, _countof(self));
    std::wstring cmd = L"\"" + std::wstring(self) + L"\" --child";
    if (argc >= 4 && !wcscmp(argv[1], L"--spawn-network")) {
        cmd = L"\"" + std::wstring(self) + L"\" --network " + argv[2] + L" " + argv[3] + L" 2000";
    }
    STARTUPINFOW si = {sizeof(si)}; PROCESS_INFORMATION pi = {};
    bool suspended = argc > 1 && !wcscmp(argv[1], L"--spawn-suspended");
    bool ansi = argc > 1 && !wcscmp(argv[1], L"--spawn-ansi");
    BOOL created;
    if (ansi) {
        char appA[32768], cmdA[32768];
        WideCharToMultiByte(CP_ACP, 0, self, -1, appA, sizeof(appA), nullptr, nullptr);
        WideCharToMultiByte(CP_ACP, 0, cmd.c_str(), -1, cmdA, sizeof(cmdA), nullptr, nullptr);
        STARTUPINFOA sa = {sizeof(sa)};
        created = CreateProcessA(appA, cmdA, nullptr, nullptr, FALSE, 0, nullptr, nullptr, &sa, &pi);
    } else created = CreateProcessW(self, cmd.data(), nullptr, nullptr, FALSE, suspended ? CREATE_SUSPENDED : 0, nullptr, nullptr, &si, &pi);
    if (!created) { printf("CHILD_ERROR=%lu\n", GetLastError()); return 1; }
    if (suspended) {
        if (WaitForSingleObject(pi.hProcess, 200) != WAIT_TIMEOUT) return 7;
        if (ResumeThread(pi.hThread) == DWORD(-1)) return 8;
        printf("SUSPENDED_CHILD_OK\n");
    }
    WaitForSingleObject(pi.hProcess, 10000); DWORD childExit = 0; GetExitCodeProcess(pi.hProcess, &childExit);
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess); if (childExit) return (int)childExit;
    if (GetEnvironmentVariableW(L"REGION_LAUNCHER_LOCALE", nullptr, 0)) {
        WCHAR buffer[32768]; if (GetEnvironmentVariableW(L"REGION_PROBE_FILE", buffer, _countof(buffer))) {
            HANDLE f = CreateFileW(buffer, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (f == INVALID_HANDLE_VALUE) { printf("FILE_ERROR=%lu\n", GetLastError()); return 1; }
            DWORD written; WriteFile(f, "host file access verified", 25, &written, nullptr); CloseHandle(f); printf("FILE_OK\n");
        }
    }
    return 0;
}
