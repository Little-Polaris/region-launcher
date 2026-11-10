#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <string>
#include <stdio.h>
#include "ConnectBroker.h"
#include "UnicodeInjection.h"

// Exercise the broker independently while an older GUI session still owns WinDivert.
int wmain(int argc, wchar_t** argv) {
    if (argc != 5) return 2;
    std::wstring name = L"Local\\RegionLauncher.fixture" + std::to_wstring(GetCurrentProcessId());
    HANDLE job = CreateJobObjectW(nullptr, name.c_str());
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = {}; limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!job || !SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) return 3;
    auto pipe = ConnectPipe(name);
    if (!StartConnectBroker(pipe, job, (USHORT)_wtoi(argv[4]))) return 4;
    SetEnvironmentVariableW(L"REGION_LAUNCHER_LOCALE", L"en-US");
    SetEnvironmentVariableW(L"REGION_LAUNCHER_REGION", L"US");
    SetEnvironmentVariableW(L"REGION_LAUNCHER_GEO", L"244");
    SetEnvironmentVariableW(L"REGION_LAUNCHER_TIMEZONE", L"Pacific Standard Time");
    SetEnvironmentVariableW(L"REGION_LAUNCHER_NETWORK", L"1");
    SetEnvironmentVariableW(L"REGION_LAUNCHER_CONNECT_PIPE", pipe.c_str());
    std::wstring command = L"\"" + std::wstring(argv[2]) + L"\" " + argv[3];
    STARTUPINFOW startup = {sizeof(startup)}; PROCESS_INFORMATION child = {};
    bool ok = CreateProcessW(argv[2], command.data(), nullptr, nullptr, TRUE, CREATE_SUSPENDED, nullptr, nullptr, &startup, &child);
    if (!ok) fwprintf(stderr, L"prepare error=%lu\n", GetLastError());
    if (ok) ok = AssignProcessToJobObject(job, child.hProcess);
    if (!ok) fwprintf(stderr, L"fixture job error=%lu\n", GetLastError());
    if (ok) ok = InjectUnicodeDll(child.hProcess, argv[1]);
    if (!ok) fwprintf(stderr, L"inject error=%lu\n", GetLastError());
    if (ok) { ResumeThread(child.hThread); WaitForSingleObject(child.hProcess, 60000); }
    DWORD status = 1;
    if (child.hProcess) GetExitCodeProcess(child.hProcess, &status);
    if (!ok && child.hProcess) { TerminateProcess(child.hProcess, 1); WaitForSingleObject(child.hProcess, 5000); }
    TerminateJobObject(job, 1);
    StopConnectBroker();
    if (child.hThread) CloseHandle(child.hThread); if (child.hProcess) CloseHandle(child.hProcess);
    CloseHandle(job); return ok && status != STILL_ACTIVE ? status : 1;
}
