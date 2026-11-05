#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <sddl.h>
#include <aclapi.h>
#include <stdio.h>
#include <string>
#include <vector>
#include "NetworkProtocol.h"
#include "ProxyBridge.h"

static HANDLE shutdownEvent, activeWorker, activeJob;
static SERVICE_STATUS_HANDLE statusHandle;
static SERVICE_STATUS status = {SERVICE_WIN32_OWN_PROCESS};
static std::wstring allowedSid;
static FILE* networkLog;
static HANDLE scopeJob;
static std::wstring directory() {
    WCHAR path[32768]; GetModuleFileNameW(nullptr, path, _countof(path));
    std::wstring s(path); return s.substr(0, s.find_last_of(L"\\"));
}
static void logLine(const char* line) { if (networkLog) { fprintf(networkLog, "%s\n", line); fflush(networkLog); } }
static BOOL belongs(DWORD pid) {
    HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    BOOL yes = FALSE; if (p) { IsProcessInJob(p, scopeJob, &yes); CloseHandle(p); } return yes;
}
static void connection(const char* name, DWORD pid, const char* host, UINT16 port, const char* info) {
    if (!belongs(pid)) return;
    char line[1024]; snprintf(line, sizeof(line), "PID=%lu %s %s:%u %s", pid, name, host, port, info); logLine(line);
}
static void report(DWORD state, DWORD error = 0) {
    status.dwCurrentState = state; status.dwWin32ExitCode = error;
    status.dwControlsAccepted = state == SERVICE_RUNNING ? SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN : 0;
    status.dwWaitHint = state == SERVICE_STOP_PENDING ? 20000 : 0;
    status.dwCheckPoint = state == SERVICE_STOP_PENDING ? 1 : 0;
    SetServiceStatus(statusHandle, &status);
}
static DWORD WINAPI control(DWORD code, DWORD, void*, void*) {
    if (code == SERVICE_CONTROL_STOP || code == SERVICE_CONTROL_SHUTDOWN) {
        report(SERVICE_STOP_PENDING); SetEvent(shutdownEvent);
    }
    return NO_ERROR;
}
static bool userMatches(HANDLE token) {
    DWORD n = 0; GetTokenInformation(token, TokenUser, nullptr, 0, &n);
    std::vector<BYTE> data(n);
    if (!GetTokenInformation(token, TokenUser, data.data(), n, &n)) return false;
    LPWSTR sid = nullptr; if (!ConvertSidToStringSidW(((TOKEN_USER*)data.data())->User.Sid, &sid)) return false;
    bool matches = allowedSid == sid; LocalFree(sid);
    GetTokenInformation(token, TokenIntegrityLevel, nullptr, 0, &n); data.resize(n);
    if (!GetTokenInformation(token, TokenIntegrityLevel, data.data(), n, &n)) return false;
    PSID integrity = ((TOKEN_MANDATORY_LABEL*)data.data())->Label.Sid;
    return matches && *GetSidSubAuthority(integrity, *GetSidSubAuthorityCount(integrity)-1) >= SECURITY_MANDATORY_MEDIUM_RID;
}
static bool owned(HANDLE object) {
    PSID owner = nullptr; PSECURITY_DESCRIPTOR sd = nullptr;
    if (GetSecurityInfo(object, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION, &owner, nullptr, nullptr, nullptr, &sd)) return false;
    LPWSTR sid = nullptr; bool ok = ConvertSidToStringSidW(owner, &sid) && allowedSid == sid;
    if (sid) LocalFree(sid); LocalFree(sd); return ok;
}
static void finishWorker() {
    if (activeWorker) { CloseHandle(activeWorker); activeWorker = nullptr; }
    if (activeJob) { CloseHandle(activeJob); activeJob = nullptr; }
}
static NetworkReply startWorker(HANDLE pipe, const NetworkRequest& request) {
    NetworkReply reply;
    if (request.version != ProtocolVersion || !request.port || request.port > 65535 || request.proxyLoopback > 1 ||
        (request.dns && (ntohl(request.dns) >> 24) != 127)) { reply.error = ERROR_INVALID_PARAMETER; return reply; }
    if (activeWorker && WaitForSingleObject(activeWorker, 0) != WAIT_OBJECT_0) { reply.error = ERROR_BUSY; return reply; }
    finishWorker();
    ULONG pid = 0;
    if (!GetNamedPipeClientProcessId(pipe, &pid) || !ImpersonateNamedPipeClient(pipe)) { reply.error = ERROR_ACCESS_DENIED; return reply; }
    HANDLE token = nullptr, client = nullptr;
    bool authorized = OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &token) && userMatches(token);
    if (token) CloseHandle(token);
    // Opening while impersonating avoids granting access the caller did not have.
    if (authorized) client = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid);
    if (!RevertToSelf()) ExitProcess(ERROR_ACCESS_DENIED);
    if (!client) { reply.error = ERROR_ACCESS_DENIED; return reply; }
    token = nullptr;
    authorized = OpenProcessToken(client, TOKEN_QUERY, &token) && userMatches(token);
    if (token) CloseHandle(token);
    HANDLE handles[6] = {}; // job, ready, stop, actual client process, service shutdown, service process
    auto cleanup = [&]() { for (HANDLE h : handles) if (h) CloseHandle(h); CloseHandle(client); };
    const uint64_t source[] = {request.job, request.ready, request.stop};
    const DWORD access[] = {JOB_OBJECT_QUERY | JOB_OBJECT_TERMINATE | READ_CONTROL, EVENT_MODIFY_STATE | READ_CONTROL, SYNCHRONIZE | READ_CONTROL};
    for (int i = 0; authorized && i < 3; ++i) {
        authorized = DuplicateHandle(client, (HANDLE)(uintptr_t)source[i], GetCurrentProcess(), &handles[i], access[i], TRUE, 0) && owned(handles[i]);
    }
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = {}; JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting = {};
    authorized = authorized && QueryInformationJobObject(handles[0], JobObjectExtendedLimitInformation, &limits, sizeof(limits), nullptr) &&
        QueryInformationJobObject(handles[0], JobObjectBasicAccountingInformation, &accounting, sizeof(accounting), nullptr) && !accounting.TotalProcesses &&
        (limits.BasicLimitInformation.LimitFlags & JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) &&
        !(limits.BasicLimitInformation.LimitFlags & (JOB_OBJECT_LIMIT_BREAKAWAY_OK | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK)) && ResetEvent(handles[1]);
    if (!authorized) { reply.error = ERROR_ACCESS_DENIED; cleanup(); return reply; }
    if (!DuplicateHandle(GetCurrentProcess(), client, GetCurrentProcess(), &handles[3], SYNCHRONIZE, TRUE, 0) ||
        !DuplicateHandle(GetCurrentProcess(), shutdownEvent, GetCurrentProcess(), &handles[4], SYNCHRONIZE, TRUE, 0) ||
        !DuplicateHandle(GetCurrentProcess(), GetCurrentProcess(), GetCurrentProcess(), &handles[5], SYNCHRONIZE, TRUE, 0)) {
        reply.error = GetLastError(); cleanup(); return reply;
    }
    auto exe = directory() + L"\\RegionNetworkService.exe";
    std::wstring command = L"\"" + exe + L"\" --worker";
    for (HANDLE h : handles) command += L" " + std::to_wstring((uintptr_t)h);
    command += L" " + std::to_wstring(request.port) + L" " + std::to_wstring(request.dns) + L" " + std::to_wstring(request.proxyLoopback);
    STARTUPINFOEXW si = {}; si.StartupInfo.cb = sizeof(si);
    SIZE_T bytes = 0; InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
    std::vector<BYTE> attributes(bytes); si.lpAttributeList = (LPPROC_THREAD_ATTRIBUTE_LIST)attributes.data();
    if (!InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, &bytes)) { reply.error = GetLastError(); cleanup(); return reply; }
    PROCESS_INFORMATION pi = {};
    bool created = UpdateProcThreadAttribute(si.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, sizeof(handles), nullptr, nullptr) &&
        CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, TRUE, EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW,
            nullptr, directory().c_str(), &si.StartupInfo, &pi);
    reply.error = created ? 0 : GetLastError(); DeleteProcThreadAttributeList(si.lpAttributeList);
    if (created) {
        HANDLE remote = nullptr;
        if (!DuplicateHandle(GetCurrentProcess(), pi.hProcess, client, &remote, SYNCHRONIZE, FALSE, 0)) {
            reply.error = GetLastError(); TerminateJobObject(handles[0], 1); TerminateProcess(pi.hProcess, 1); CloseHandle(pi.hProcess);
        } else {
            reply.worker = (uintptr_t)remote; activeWorker = pi.hProcess; activeJob = handles[0]; handles[0] = nullptr;
        }
        CloseHandle(pi.hThread);
    }
    cleanup(); return reply;
}
static void WINAPI serviceMain(DWORD, LPWSTR*) {
    statusHandle = RegisterServiceCtrlHandlerExW(ServiceName, control, nullptr); if (!statusHandle) return;
    shutdownEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!shutdownEvent) { report(SERVICE_STOPPED, GetLastError()); return; }
    WCHAR sid[256]; DWORD size = sizeof(sid);
    auto key = L"SYSTEM\\CurrentControlSet\\Services\\RegionLauncherNetwork\\Parameters";
    if (RegGetValueW(HKEY_LOCAL_MACHINE, key, L"AllowedSid", RRF_RT_REG_SZ, nullptr, sid, &size)) { report(SERVICE_STOPPED, ERROR_ACCESS_DENIED); return; }
    allowedSid = sid; PSID parsed = nullptr;
    if (!ConvertStringSidToSidW(sid, &parsed)) { report(SERVICE_STOPPED, ERROR_INVALID_SID); return; } LocalFree(parsed);
    std::wstring sddl = L"D:P(A;;GA;;;SY)(A;;GRGW;;;" + allowedSid + L")";
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &sd, nullptr)) { report(SERVICE_STOPPED, GetLastError()); return; }
    SECURITY_ATTRIBUTES sa = {sizeof(sa), sd, FALSE};
    HANDLE pipe = CreateNamedPipeW(PipeName, PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 4096, 4096, 5000, &sa);
    LocalFree(sd);
    if (pipe == INVALID_HANDLE_VALUE) { report(SERVICE_STOPPED, GetLastError()); return; }
    report(SERVICE_RUNNING);
    DWORD failure = 0;
    while (WaitForSingleObject(shutdownEvent, 0) != WAIT_OBJECT_0) {
        OVERLAPPED ov = {}; ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        BOOL connected = ConnectNamedPipe(pipe, &ov); DWORD err = connected ? 0 : GetLastError();
        if (err == ERROR_IO_PENDING) {
            HANDLE waits[] = {ov.hEvent, shutdownEvent};
            DWORD waited = WaitForMultipleObjects(2, waits, FALSE, INFINITE), count = 0;
            if (waited == WAIT_OBJECT_0) connected = GetOverlappedResult(pipe, &ov, &count, FALSE);
            else { CancelIoEx(pipe, &ov); GetOverlappedResult(pipe, &ov, &count, TRUE); }
        } else if (err == ERROR_PIPE_CONNECTED) connected = TRUE;
        else if (!connected) failure = err;
        CloseHandle(ov.hEvent);
        if (WaitForSingleObject(shutdownEvent, 0) == WAIT_OBJECT_0 || failure) break;
        if (connected) {
            NetworkRequest request = {};
            if (PipeIO(pipe, &request, sizeof(request), false, shutdownEvent)) {
                auto reply = startWorker(pipe, request);
                BYTE ack = 0;
                bool delivered = PipeIO(pipe, &reply, sizeof(reply), true, shutdownEvent) && PipeIO(pipe, &ack, 1, false, shutdownEvent) && ack == 1;
                if (!delivered && !reply.error && activeJob) {
                    TerminateJobObject(activeJob, 1); TerminateProcess(activeWorker, 1);
                }
            }
        }
        DisconnectNamedPipe(pipe);
    }
    report(SERVICE_STOP_PENDING); SetEvent(shutdownEvent);
    // Always stop selected applications before withdrawing their packet interception.
    if (activeJob) TerminateJobObject(activeJob, 1);
    if (activeWorker && WaitForSingleObject(activeWorker, 15000) != WAIT_OBJECT_0) TerminateProcess(activeWorker, 1);
    finishWorker(); CloseHandle(pipe); CloseHandle(shutdownEvent); report(SERVICE_STOPPED, failure);
}
static int worker(int argc, WCHAR** argv) {
    if (argc != 11) return ERROR_INVALID_PARAMETER;
    HANDLE h[6]; for (int i=0;i<6;++i) h[i]=(HANDLE)(uintptr_t)_wcstoui64(argv[i+2], nullptr, 10);
    scopeJob=h[0]; auto port=wcstoul(argv[8],nullptr,10), dns=wcstoul(argv[9],nullptr,10), loopback=wcstoul(argv[10],nullptr,10);
    if (!port || port>65535 || loopback>1 || (dns && (ntohl(dns)>>24)!=127)) return ERROR_INVALID_PARAMETER;
    // Only this protected installation supplies privileged code and logs.
    _wfopen_s(&networkLog, (directory()+L"\\logs\\network.log").c_str(), L"wb");
    HMODULE core=LoadLibraryExW((directory()+L"\\ProxyBridgeCore.dll").c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!core) return GetLastError();
#define IMPORT(fn) auto fn=reinterpret_cast<decltype(&::fn)>(GetProcAddress(core,#fn)); if(!fn) return ERROR_PROC_NOT_FOUND
    IMPORT(ProxyBridge_SetProcessScope); IMPORT(ProxyBridge_SetLogCallback); IMPORT(ProxyBridge_SetTrafficLoggingEnabled);
    IMPORT(ProxyBridge_SetConnectionCallback);
    IMPORT(ProxyBridge_AddProxyConfig); IMPORT(ProxyBridge_AddRule); IMPORT(ProxyBridge_SetDnsResolver); IMPORT(ProxyBridge_Start); IMPORT(ProxyBridge_Stop);
#undef IMPORT
    ProxyBridge_SetProcessScope(belongs); ProxyBridge_SetLogCallback(logLine); ProxyBridge_SetTrafficLoggingEnabled(TRUE);
    ProxyBridge_SetConnectionCallback(connection);
    auto id=ProxyBridge_AddProxyConfig(PROXY_TYPE_SOCKS5,"127.0.0.1",(UINT16)port,"","",FALSE);
    bool ok=id && ProxyBridge_AddRule("*","::/0","*","*",RULE_PROTOCOL_BOTH,RULE_ACTION_BLOCK,0,TRUE);
    if(dns) {
        IN_ADDR addr={}; addr.S_un.S_addr=dns; char text[INET_ADDRSTRLEN];
        ok=ok && InetNtopA(AF_INET,&addr,text,sizeof(text)) && ProxyBridge_SetDnsResolver(text) &&
            ProxyBridge_AddRule("*","*","53","*",RULE_PROTOCOL_BOTH,RULE_ACTION_PROXY,id,TRUE);
    }
    ok=ok && ProxyBridge_AddRule("*","*","*","*",RULE_PROTOCOL_BOTH,RULE_ACTION_PROXY,id,loopback) && ProxyBridge_Start();
    if(!ok) { logLine("Network engine startup failed"); return ERROR_NOT_READY; }
    SetEvent(h[1]); HANDLE waits[]={h[2],h[3],h[4],h[5]}; WaitForMultipleObjects(4,waits,FALSE,INFINITE);
    TerminateJobObject(scopeJob,1); ProxyBridge_Stop(); if(networkLog) fclose(networkLog);
    for(HANDLE handle:h) CloseHandle(handle); return 0;
}
int wmain(int argc, WCHAR** argv) {
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_APPLICATION_DIR);
    if(argc>1 && !wcscmp(argv[1],L"--worker")) return worker(argc,argv);
    SERVICE_TABLE_ENTRYW table[]={{const_cast<LPWSTR>(ServiceName),serviceMain},{nullptr,nullptr}};
    return StartServiceCtrlDispatcherW(table) ? 0 : (int)GetLastError();
}
