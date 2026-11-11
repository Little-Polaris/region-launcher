#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include <string>
#include <vector>
#include <filesystem>
#include "NetworkProtocol.h"
#include "UnicodeInjection.h"
#include "ConnectBroker.h"

static HANDLE sessionJob;
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
static void cleanupLinkProfiles(const std::wstring& job) {
    namespace fs = std::filesystem;
    auto temp = get(L"LinkBrowser_TEMP");
    const std::wstring prefix = L"Local\\RegionLauncher.";
    if (temp.empty() || job.rfind(prefix, 0) != 0) return;
    auto id = job.substr(prefix.size());
    if (id.size() != 32 || id.find_first_not_of(L"0123456789abcdefABCDEF") != std::wstring::npos) return;
    fs::path target = fs::path(temp).parent_path();
    if (!target.is_absolute() || target.filename() != id || target.parent_path().filename() != L"link-browser-temp") return;
    // Never follow a junction anywhere along the temporary profile path.
    for (auto p = target; !p.empty(); p = p.parent_path()) {
        DWORD attributes = GetFileAttributesW(p.c_str());
        if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) return;
        if (p == p.parent_path()) break;
    }
    auto erase = [&](auto&& self, const fs::path& path) -> bool {
        DWORD attributes = GetFileAttributesW(path.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES) return true;
        if (!(attributes & FILE_ATTRIBUTE_DIRECTORY)) return DeleteFileW(path.c_str()) != 0;
        if (!(attributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
            std::error_code ec;
            for (fs::directory_iterator it(path, ec), end; !ec && it != end; it.increment(ec))
                if (!self(self, it->path())) return false;
            if (ec) return false;
        }
        return RemoveDirectoryW(path.c_str()) != 0;
    };
    for (int retry = 0; retry < 20; ++retry) {
        if (erase(erase, target)) return;
        Sleep(100);
    }
    fwprintf(stderr, L"[LinkBrowser] temporary profile cleanup deferred\n");
}
static std::string utf8(const std::wstring& value) {
    int n = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string out(n, 0); WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, out.data(), n, nullptr, nullptr); return out;
}
static std::wstring directory() {
    WCHAR path[32768]; GetModuleFileNameW(nullptr, path, _countof(path)); std::wstring s(path); return s.substr(0, s.find_last_of(L"\\"));
}
static int error(const WCHAR* what) {
    DWORD code = GetLastError();
    fwprintf(stderr, L"%s (Windows error %lu)\n", what, code); fflush(stderr);
    FILE* f = nullptr; _wfopen_s(&f, (cfg + L".error.log").c_str(), L"a, ccs=UTF-8");
    if (f) { fwprintf(f, L"PID=%lu %s (Windows error %lu)\n", GetCurrentProcessId(), what, code); fclose(f); }
    return 1;
}
static HANDLE requestNetwork(HANDLE ready, HANDLE stop) {
    NetworkRequest request;
    if (get(L"ProxyHost", L"127.0.0.1") != L"127.0.0.1") { SetLastError(ERROR_INVALID_PARAMETER); return nullptr; }
    request.port = wcstoul(get(L"ProxyPort", L"7890").c_str(), nullptr, 10);
    request.proxyLoopback = get(L"ProxyLoopback", L"0") == L"1";
    request.job = (uintptr_t)sessionJob; request.ready = (uintptr_t)ready; request.stop = (uintptr_t)stop;
    auto dns = get(L"DnsAddress"); IN_ADDR address = {};
    if (!dns.empty()) {
        if (InetPtonW(AF_INET, dns.c_str(), &address) != 1) { SetLastError(ERROR_INVALID_PARAMETER); return nullptr; }
        request.dns = address.S_un.S_addr;
    }
    if (!WaitNamedPipeW(PipeName, 5000)) return nullptr;
    HANDLE pipe = CreateFileW(PipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IMPERSONATION, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) return nullptr;
    // Verify the pipe belongs to the installed service, before sending process handles.
    SC_HANDLE manager = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
    SC_HANDLE service = manager ? OpenServiceW(manager, ServiceName, SERVICE_QUERY_STATUS) : nullptr;
    SERVICE_STATUS_PROCESS status = {}; DWORD bytes = 0; ULONG server = 0;
    bool trusted = service && QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, (BYTE*)&status, sizeof(status), &bytes) &&
        status.dwCurrentState == SERVICE_RUNNING && GetNamedPipeServerProcessId(pipe, &server) && server == status.dwProcessId;
    if (service) CloseServiceHandle(service); if (manager) CloseServiceHandle(manager);
    NetworkReply reply;
    bool ok = trusted && PipeIO(pipe, &request, sizeof(request), true) && PipeIO(pipe, &reply, sizeof(reply), false);
    DWORD err = trusted ? GetLastError() : ERROR_ACCESS_DENIED;
    BYTE ack = 1;
    if (ok) PipeIO(pipe, &ack, 1, true);
    CloseHandle(pipe);
    if (!ok) { SetLastError(err); return nullptr; }
    if (reply.version != ProtocolVersion || reply.error || !reply.worker) {
        SetLastError(reply.error ? reply.error : ERROR_INVALID_DATA); return nullptr;
    }
    return (HANDLE)(uintptr_t)reply.worker;
}
static bool x64Image(const std::wstring& path) {
    FILE* f = nullptr; if (_wfopen_s(&f, path.c_str(), L"rb") || !f) return false;
    IMAGE_DOS_HEADER dos = {}; bool valid = fread(&dos, sizeof(dos), 1, f) == 1 && dos.e_magic == IMAGE_DOS_SIGNATURE && dos.e_lfanew > 0;
    DWORD signature = 0; IMAGE_FILE_HEADER header = {};
    if (valid) valid = _fseeki64(f, dos.e_lfanew, SEEK_SET) == 0 && fread(&signature, sizeof(signature), 1, f) == 1 &&
        fread(&header, sizeof(header), 1, f) == 1 && signature == IMAGE_NT_SIGNATURE && header.Machine == IMAGE_FILE_MACHINE_AMD64;
    fclose(f); return valid;
}
static bool configureEnvironment() {
    auto selectedLocale = get(L"Locale", L"en-US"), selectedRegion = get(L"Region", L"US");
    auto selectedZone = get(L"TimeZone", L"Pacific Standard Time"), geoText = get(L"Geo", L"244");
    auto automatic = get(L"AutoDst", L"1"); WCHAR iso[16] = {};
    DYNAMIC_TIME_ZONE_INFORMATION zone = {}; bool found = false;
    for (DWORD i = 0; EnumDynamicTimeZoneInformation(i, &zone) == ERROR_SUCCESS; ++i) {
        if (!_wcsicmp(zone.TimeZoneKeyName, selectedZone.c_str())) { found = true; break; }
    }
    if (!found || !IsValidLocaleName(selectedLocale.c_str()) || (automatic != L"0" && automatic != L"1") ||
        !GetGeoInfoW(_wtoi(geoText.c_str()), GEO_ISO2, iso, _countof(iso), 0) || _wcsicmp(iso, selectedRegion.c_str())) {
        SetLastError(ERROR_INVALID_PARAMETER); return false;
    }
    WCHAR tz[128] = {};
    if (automatic == L"1") {
        HMODULE icu = LoadLibraryExW(L"icu.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        using MapZone = int32_t (__cdecl*)(const WCHAR*, int32_t, const char*, WCHAR*, int32_t, int32_t*);
        auto map = icu ? reinterpret_cast<MapZone>(GetProcAddress(icu, "ucal_getTimeZoneIDForWindowsID")) : nullptr;
        int32_t status = 0;
        bool mapped = map && map(selectedZone.c_str(), -1, utf8(selectedRegion).c_str(), tz, _countof(tz), &status) > 0 && status <= 0;
        if (icu) FreeLibrary(icu);
        if (!mapped) { SetLastError(ERROR_INVALID_DATA); return false; }
    } else {
        LONG bias = zone.Bias + zone.StandardBias;
        if (bias % 60 == 0) swprintf_s(tz, L"Etc/GMT%+ld", bias / 60);
        else swprintf_s(tz, L"GMT%c%02ld:%02ld", bias > 0 ? L'-' : L'+', labs(bias)/60, labs(bias)%60);
    }
    SetEnvironmentVariableW(L"REGION_LAUNCHER_LOCALE", selectedLocale.c_str());
    SetEnvironmentVariableW(L"REGION_LAUNCHER_TIMEZONE", selectedZone.c_str());
    SetEnvironmentVariableW(L"REGION_LAUNCHER_REGION", selectedRegion.c_str());
    SetEnvironmentVariableW(L"REGION_LAUNCHER_GEO", geoText.c_str());
    SetEnvironmentVariableW(L"REGION_LAUNCHER_DST", automatic.c_str());
    SetEnvironmentVariableW(L"REGION_LAUNCHER_DNS_ADDRESS", get(L"DnsAddress").empty() ? nullptr : get(L"DnsAddress").c_str());
    SetEnvironmentVariableW(L"REGION_LAUNCHER_JOB", get(L"Job").c_str());
    for (auto key : {L"ENABLED", L"EXE", L"PROFILE", L"TEMP", L"PROFILE_NAME"}) {
        auto name = std::wstring(L"REGION_LAUNCHER_LINK_") + key;
        auto value = get((std::wstring(L"LinkBrowser_") + key).c_str());
        SetEnvironmentVariableW(name.c_str(), value.empty() ? nullptr : value.c_str());
    }
    SetEnvironmentVariableW(L"TZ", tz);
    for (WCHAR& c : selectedLocale) if (c == L'-') c = L'_';
    selectedLocale += L".UTF-8";
    SetEnvironmentVariableW(L"LANG", selectedLocale.c_str()); SetEnvironmentVariableW(L"LC_ALL", selectedLocale.c_str());
    return true;
}
static int run(bool localeOnly, bool joining = false, bool inject = true) {
    struct BrokerCleanup { ~BrokerCleanup() { StopConnectBroker(); } } brokerCleanup;
    auto app = get(L"Application"); if (!x64Image(app)) { SetLastError(ERROR_BAD_EXE_FORMAT); return error(L"This build requires an x64 Windows executable"); }
    if (!configureEnvironment()) return error(L"Invalid Windows country, language or time zone settings");
    SetEnvironmentVariableW(L"REGION_LAUNCHER_NETWORK", localeOnly ? nullptr : L"1");
    if (!localeOnly) {
        // Windows environment names are case-insensitive; only this process and
        // its children change. The host proxy remains available to outside apps.
        for (auto name : {L"HTTP_PROXY", L"HTTPS_PROXY", L"ALL_PROXY", L"NO_PROXY"}) SetEnvironmentVariableW(name, nullptr);
    }
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
        engine = requestNetwork(ready, stop);
        if (!engine) return error(L"Network service unavailable or request rejected; install/start RegionLauncherNetwork first (application was not started)");
        HANDLE waits[] = {ready, engine, owner};
        DWORD wait = WaitForMultipleObjects(owner ? 3 : 2, waits, FALSE, 30000);
        if (wait != WAIT_OBJECT_0) { requestStop(); SetLastError(wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : ERROR_NOT_READY); return error(L"Network engine not ready or launcher closed; application was not started. See the installed service logs/network.log"); }
    }
    if (!localeOnly && inject) {
        auto pipe = ConnectPipe(name);
        if (!joining && !StartConnectBroker(pipe, sessionJob, (USHORT)wcstoul(get(L"ProxyPort", L"7890").c_str(), nullptr, 10))) {
            requestStop(); return error(L"Cannot start private connection broker; application was not started");
        }
        SetEnvironmentVariableW(L"REGION_LAUNCHER_CONNECT_PIPE", pipe.c_str());
    } else SetEnvironmentVariableW(L"REGION_LAUNCHER_CONNECT_PIPE", nullptr);
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
    BOOL created = CreateProcessW(app.c_str(), command.data(), nullptr, nullptr, TRUE,
        CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, nullptr, cwd.c_str(), &si, &pi);
    DWORD createError = GetLastError();
    for (HANDLE handle : stdCopies) if (handle) CloseHandle(handle);
    SetLastError(createError);
    if (!created) { requestStop(); return error(L"Could not prepare application with locale module"); }
    auto abortApplication = [&](const WCHAR* reason) {
        DWORD failure = GetLastError(); TerminateProcess(pi.hProcess, failure); WaitForSingleObject(pi.hProcess, 5000);
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess); requestStop(); SetLastError(failure);
        return error(reason);
    };
    if (!AssignProcessToJobObject(sessionJob, pi.hProcess)) {
        return abortApplication(L"Could not enroll application; it was not resumed");
    }
    if (inject && !InjectUnicodeDll(pi.hProcess, directory() + L"\\RegionHook.dll")) {
        return abortApplication(L"Could not load the Unicode runtime module; application main thread was not resumed");
    }
    if (ResumeThread(pi.hThread) == DWORD(-1)) return abortApplication(L"Could not resume application");
    CloseHandle(pi.hThread);
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
    StopConnectBroker();
    if (!joining) cleanupLinkProfiles(name);
    CloseHandle(pi.hProcess); CloseHandle(sessionJob); CloseHandle(ready); CloseHandle(stop);
    if (owner) CloseHandle(owner);
    return rootExit == STILL_ACTIVE ? 1 : (int)rootExit;
}
int wmain(int argc, wchar_t** argv) {
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (argc < 3) { fwprintf(stderr, L"Usage: RegionSession --run|--locale-test session.ini\n"); return 2; }
    WCHAR full[32768]; if (!GetFullPathNameW(argv[2], _countof(full), full, nullptr)) return 2; cfg = full;
    if (!_wcsicmp(argv[1], L"--run")) return run(false);
    if (!_wcsicmp(argv[1], L"--join")) return run(false, true);
    if (!_wcsicmp(argv[1], L"--locale-test")) return run(true);
    // Fixture-only: exercise the packet engine independently of API instrumentation.
    if (!_wcsicmp(argv[1], L"--network-test")) return run(false, false, false);
    return 2;
}
