#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include <string>
#include <vector>
#include "detours.h"
#include "ProxyBridge.h"

static HANDLE sessionJob;
static FILE* brokerLog;
static std::wstring cfg;
static std::wstring get(const WCHAR* key, const WCHAR* fallback = L"") {
    WCHAR value[32768]; GetPrivateProfileStringW(L"session", key, fallback, value, _countof(value), cfg.c_str()); return value;
}
static std::wstring quote(const std::wstring& value) {
    std::wstring out = L"\""; size_t slashes = 0;
    for (auto c : value) {
        if (c == L'\\') { ++slashes; continue; }
        if (c == L'\"') out.append(slashes * 2 + 1, L'\\'); else out.append(slashes, L'\\');
        slashes = 0; out += c;
    }
    out.append(slashes * 2, L'\\'); return out + L'\"';
}
static std::string utf8(const std::wstring& value) {
    int n = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string out(n, 0); WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, out.data(), n, nullptr, nullptr); return out;
}
static std::wstring directory() {
    WCHAR path[32768]; GetModuleFileNameW(nullptr, path, _countof(path)); std::wstring s(path); return s.substr(0, s.find_last_of(L"\\"));
}
static void logLine(const char* text) { if (brokerLog) { fprintf(brokerLog, "%s\n", text); fflush(brokerLog); } }
static BOOL belongs(DWORD pid) {
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return FALSE;
    BOOL inJob = FALSE; IsProcessInJob(process, sessionJob, &inJob); CloseHandle(process); return inJob;
}
static void connection(const char* name, DWORD pid, const char* host, UINT16 port, const char* info) {
    if (!belongs(pid)) return;
    char line[2048]; snprintf(line, sizeof(line), "PID=%lu %s %s:%u %s", pid, name, host, port, info); logLine(line);
}
static int error(const WCHAR* what) {
    DWORD code = GetLastError();
    fwprintf(stderr, L"%s (Windows error %lu)\n", what, code); fflush(stderr);
    FILE* f = nullptr; _wfopen_s(&f, (cfg + L".error.log").c_str(), L"a, ccs=UTF-8");
    if (f) { fwprintf(f, L"PID=%lu %s (Windows error %lu)\n", GetCurrentProcessId(), what, code); fclose(f); }
    return 1;
}
static int broker(DWORD parent) {
    auto name = get(L"Job");
    if (name.rfind(L"Local\\RegionLauncher.", 0) != 0) { SetLastError(ERROR_INVALID_DATA); return error(L"Invalid job identifier in configuration"); }
    HANDLE singleton = CreateMutexW(nullptr, TRUE, L"Local\\RegionLauncher.NetworkEngine");
    if (!singleton || GetLastError() == ERROR_ALREADY_EXISTS) return error(L"Another launcher network session is running");
    sessionJob = OpenJobObjectW(JOB_OBJECT_QUERY | JOB_OBJECT_TERMINATE, FALSE, name.c_str());
    HANDLE parentHandle = OpenProcess(SYNCHRONIZE, FALSE, parent);
    HANDLE ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, (name + L".ready").c_str());
    HANDLE stop = OpenEventW(SYNCHRONIZE, FALSE, (name + L".stop").c_str());
    if (!sessionJob || !parentHandle || !ready || !stop) return error(L"Cannot open session controls");
    _wfopen_s(&brokerLog, (cfg + L".network.log").c_str(), L"wb");
    HMODULE core = LoadLibraryW((directory() + L"\\ProxyBridgeCore.dll").c_str());
    if (!core) return error(L"Cannot load proxy engine");
#define IMPORT(fn) auto fn = reinterpret_cast<decltype(&::fn)>(GetProcAddress(core, #fn)); if (!fn) return error(L"Missing proxy engine export")
    IMPORT(ProxyBridge_SetProcessScope); IMPORT(ProxyBridge_SetLogCallback); IMPORT(ProxyBridge_SetConnectionCallback);
    IMPORT(ProxyBridge_SetTrafficLoggingEnabled); IMPORT(ProxyBridge_AddProxyConfig); IMPORT(ProxyBridge_AddRule);
    IMPORT(ProxyBridge_Start); IMPORT(ProxyBridge_Stop);
    IMPORT(ProxyBridge_SetDnsResolver);
#undef IMPORT
    ProxyBridge_SetProcessScope(belongs); ProxyBridge_SetLogCallback(logLine); ProxyBridge_SetConnectionCallback(connection);
    ProxyBridge_SetTrafficLoggingEnabled(TRUE);
    auto host = utf8(get(L"ProxyHost", L"127.0.0.1")); int port = _wtoi(get(L"ProxyPort", L"7890").c_str());
    if (port < 1 || port > 65535) return 2;
    auto id = ProxyBridge_AddProxyConfig(PROXY_TYPE_SOCKS5, host.c_str(), (UINT16)port, "", "", FALSE);
    BOOL proxyLoopback = get(L"ProxyLoopback", L"0") == L"1";
    if (!ProxyBridge_AddRule("*", "::/0", "*", "*", RULE_PROTOCOL_BOTH, RULE_ACTION_BLOCK, 0, TRUE))
        return error(L"IPv6 block rule could not be installed");
    auto dns = utf8(get(L"DnsAddress"));
    if (!get(L"DnsAddress").empty() &&
        (!ProxyBridge_SetDnsResolver(dns.c_str()) ||
         !ProxyBridge_AddRule("*", "*", "53", "*", RULE_PROTOCOL_BOTH, RULE_ACTION_PROXY, id, TRUE)))
        return error(L"Private DNS configuration rejected");
    if (!id || !ProxyBridge_AddRule("*", "*", "*", "*", RULE_PROTOCOL_BOTH, RULE_ACTION_PROXY, id, proxyLoopback)) return error(L"Proxy configuration rejected");
    if (!ProxyBridge_Start()) return error(L"Proxy driver could not start; see network log");
    SetEvent(ready);
    HANDLE waits[] = {parentHandle, stop}; WaitForMultipleObjects(2, waits, FALSE, INFINITE);
    // Stop the owned applications before removing their network route.
    TerminateJobObject(sessionJob, 1);
    ProxyBridge_Stop(); if (brokerLog) fclose(brokerLog);
    CloseHandle(sessionJob); CloseHandle(parentHandle); CloseHandle(ready); CloseHandle(stop); CloseHandle(singleton);
    return 0;
}
static bool x64Image(const std::wstring& path) {
    FILE* f = nullptr; if (_wfopen_s(&f, path.c_str(), L"rb") || !f) return false;
    IMAGE_DOS_HEADER dos = {}; bool valid = fread(&dos, sizeof(dos), 1, f) == 1 && dos.e_magic == IMAGE_DOS_SIGNATURE && dos.e_lfanew > 0;
    DWORD signature = 0; IMAGE_FILE_HEADER header = {};
    if (valid) valid = _fseeki64(f, dos.e_lfanew, SEEK_SET) == 0 && fread(&signature, sizeof(signature), 1, f) == 1 &&
        fread(&header, sizeof(header), 1, f) == 1 && signature == IMAGE_NT_SIGNATURE && header.Machine == IMAGE_FILE_MACHINE_AMD64;
    fclose(f); return valid;
}
static int run(bool localeOnly, bool joining = false, bool inject = true) {
    auto app = get(L"Application"); if (!x64Image(app)) { SetLastError(ERROR_BAD_EXE_FORMAT); return error(L"This build requires an x64 Windows executable"); }
    DWORD ownerPid = wcstoul(get(L"OwnerPid", L"0").c_str(), nullptr, 10);
    HANDLE owner = ownerPid ? OpenProcess(SYNCHRONIZE, FALSE, ownerPid) : nullptr;
    if (ownerPid && !owner) return error(L"Launcher is no longer running");
    auto name = get(L"Job"); if (name.rfind(L"Local\\RegionLauncher.", 0) != 0) return 2;
    sessionJob = joining ? OpenJobObjectW(JOB_OBJECT_QUERY | JOB_OBJECT_ASSIGN_PROCESS, FALSE, name.c_str()) : CreateJobObjectW(nullptr, name.c_str());
    if (!sessionJob || (!joining && GetLastError() == ERROR_ALREADY_EXISTS)) return error(L"Invalid, closed or reused session");
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = {}; limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!joining && !SetInformationJobObject(sessionJob, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) return error(L"Cannot configure process group");
    HANDLE ready = joining ? OpenEventW(SYNCHRONIZE, FALSE, (name + L".ready").c_str()) : CreateEventW(nullptr, TRUE, FALSE, (name + L".ready").c_str());
    HANDLE stop = joining ? OpenEventW(SYNCHRONIZE, FALSE, (name + L".stop").c_str()) : CreateEventW(nullptr, TRUE, FALSE, (name + L".stop").c_str());
    if (!ready || !stop || (joining && WaitForSingleObject(ready, 0) != WAIT_OBJECT_0)) return error(L"Session is not ready");
    auto requestStop = [&]() { if (!joining) SetEvent(stop); };
    HANDLE engine = nullptr;
    if (!localeOnly && !joining) {
        WCHAR self[32768]; GetModuleFileNameW(nullptr, self, _countof(self));
        auto params = L"--broker " + quote(cfg) + L" " + std::to_wstring(GetCurrentProcessId());
        SHELLEXECUTEINFOW sei = {sizeof(sei)}; sei.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC; sei.lpVerb = L"runas";
        sei.lpFile = self; sei.lpParameters = params.c_str(); sei.nShow = SW_HIDE;
        if (!ShellExecuteExW(&sei)) return error(L"Network engine elevation was cancelled or failed");
        engine = sei.hProcess; HANDLE waits[] = {ready, engine, owner};
        if (WaitForMultipleObjects(owner ? 3 : 2, waits, FALSE, 30000) != WAIT_OBJECT_0) { requestStop(); return error(L"Network engine not ready or launcher closed; application was not started"); }
    }
    SetEnvironmentVariableW(L"REGION_LAUNCHER_LOCALE", get(L"Locale", L"en-US").c_str());
    SetEnvironmentVariableW(L"REGION_LAUNCHER_TIMEZONE", get(L"TimeZone", L"Pacific Standard Time").c_str());
    SetEnvironmentVariableW(L"REGION_LAUNCHER_REGION", get(L"Region", L"US").c_str());
    SetEnvironmentVariableW(L"REGION_LAUNCHER_GEO", get(L"Geo", L"244").c_str());
    SetEnvironmentVariableW(L"REGION_LAUNCHER_DST", get(L"AutoDst", L"1").c_str());
    SetEnvironmentVariableW(L"REGION_LAUNCHER_DNS_ADDRESS", get(L"DnsAddress").empty() ? nullptr : get(L"DnsAddress").c_str());
    SetEnvironmentVariableW(L"TZ", get(L"AutoDst", L"1") == L"1" ? L"America/Los_Angeles" : L"Etc/GMT+8");
    SetEnvironmentVariableW(L"LANG", L"en_US.UTF-8");
    auto dll = utf8(directory() + L"\\RegionHook.dll");
    // Detours uses ANSI paths for its import; require an ASCII deployment path for a predictable import.
    for (unsigned char c : dll) if (c >= 128) { requestStop(); SetLastError(ERROR_INVALID_NAME); return error(L"Move launcher to a path containing only English characters"); }
    auto command = quote(app) + L" " + get(L"Arguments");
    auto cwd = get(L"WorkingDirectory"); if (cwd.empty()) cwd = app.substr(0, app.find_last_of(L"\\/"));
    STARTUPINFOW si = {sizeof(si)}; PROCESS_INFORMATION pi = {};
    HANDLE stdCopies[3] = {};
    DWORD stdKinds[] = {STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE};
    for (int i = 0; i < 3; ++i) {
        HANDLE original = GetStdHandle(stdKinds[i]);
        if (original && original != INVALID_HANDLE_VALUE)
            DuplicateHandle(GetCurrentProcess(), original, GetCurrentProcess(), &stdCopies[i], 0, TRUE, DUPLICATE_SAME_ACCESS);
    }
    si.dwFlags = STARTF_USESTDHANDLES; si.hStdInput = stdCopies[0]; si.hStdOutput = stdCopies[1]; si.hStdError = stdCopies[2];
    BOOL created = inject ? DetourCreateProcessWithDllExW(app.c_str(), command.data(), nullptr, nullptr, TRUE,
        CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, nullptr, cwd.c_str(), &si, &pi, dll.c_str(), nullptr) :
        CreateProcessW(app.c_str(), command.data(), nullptr, nullptr, TRUE,
        CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, nullptr, cwd.c_str(), &si, &pi);
    DWORD createError = GetLastError();
    for (HANDLE handle : stdCopies) if (handle) CloseHandle(handle);
    SetLastError(createError);
    if (!created) { requestStop(); return error(L"Could not prepare application with locale module"); }
    if (!AssignProcessToJobObject(sessionJob, pi.hProcess)) {
        TerminateProcess(pi.hProcess, 1); requestStop(); return error(L"Could not enroll application; it was not resumed");
    }
    ResumeThread(pi.hThread); CloseHandle(pi.hThread);
    wprintf(L"STARTED PID=%lu\n", pi.dwProcessId); fflush(stdout);
    DWORD rootExit = STILL_ACTIVE;
    if (joining) {
        HANDLE waits[] = {pi.hProcess, stop, owner}; WaitForMultipleObjects(owner ? 3 : 2, waits, FALSE, INFINITE);
    } else for (;;) {
        if (owner && WaitForSingleObject(owner, 0) == WAIT_OBJECT_0) { TerminateJobObject(sessionJob, 1); break; }
        if (engine && WaitForSingleObject(engine, 0) == WAIT_OBJECT_0) { TerminateJobObject(sessionJob, 1); break; }
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting = {};
        if (!QueryInformationJobObject(sessionJob, JobObjectBasicAccountingInformation, &accounting, sizeof(accounting), nullptr) || !accounting.ActiveProcesses) break;
        if (WaitForSingleObject(stop, 100) == WAIT_OBJECT_0) { TerminateJobObject(sessionJob, 0); break; }
    }
    GetExitCodeProcess(pi.hProcess, &rootExit); requestStop();
    if (engine) { WaitForSingleObject(engine, 10000); CloseHandle(engine); }
    CloseHandle(pi.hProcess); CloseHandle(sessionJob); CloseHandle(ready); CloseHandle(stop);
    if (owner) CloseHandle(owner);
    return rootExit == STILL_ACTIVE ? 1 : (int)rootExit;
}
int wmain(int argc, wchar_t** argv) {
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (argc < 3) { fwprintf(stderr, L"Usage: RegionSession --run|--locale-test|--broker session.ini [parentPID]\n"); return 2; }
    WCHAR full[32768]; if (!GetFullPathNameW(argv[2], _countof(full), full, nullptr)) return 2; cfg = full;
    if (!_wcsicmp(argv[1], L"--broker") && argc == 4) return broker(wcstoul(argv[3], nullptr, 10));
    if (!_wcsicmp(argv[1], L"--run")) return run(false);
    if (!_wcsicmp(argv[1], L"--join")) return run(false, true);
    if (!_wcsicmp(argv[1], L"--locale-test")) return run(true);
    // Fixture-only: exercise the packet engine independently of API instrumentation.
    if (!_wcsicmp(argv[1], L"--network-test")) return run(false, false, false);
    return 2;
}
