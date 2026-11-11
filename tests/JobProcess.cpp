#include "../src/JobProcess.h"
#include <cstdio>
#include <string>
#include <stdexcept>

static void require(bool ok, const char* what) {
    if (!ok) throw std::runtime_error(std::string(what) + " (error " + std::to_string(GetLastError()) + ")");
}
struct Child {
    PROCESS_INFORMATION pi = {};
    ~Child() { if (pi.hProcess) { TerminateProcess(pi.hProcess, 0); WaitForSingleObject(pi.hProcess, 5000);
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess); } }
};
static void start(Child& child, HANDLE job, const WCHAR* app, const std::wstring& args = L"") {
    std::wstring command = L"\"" + std::wstring(app) + L"\" " + args;
    STARTUPINFOW si = {sizeof(si)};
    require(CreateSuspendedJobProcess(job, app, command.data(), nullptr, si, &child.pi), "create in job");
    BOOL member = FALSE;
    require(IsProcessInJob(child.pi.hProcess, job, &member) && member, "enrolled before resume");
    require(WaitForSingleObject(child.pi.hProcess, 50) == WAIT_TIMEOUT, "suspended child remains alive");
}
static int run(int argc, WCHAR** argv) {
    if (argc == 3 && !wcscmp(argv[1], L"--child")) {
        HANDLE event = OpenEventW(EVENT_MODIFY_STATE, FALSE, argv[2]);
        if (!event) return 2;
        SetEvent(event); CloseHandle(event); Sleep(60000); return 0;
    }
    WCHAR self[32768]; require(GetModuleFileNameW(nullptr, self, _countof(self)), "self path");
    // An unrelated process must survive both explicit job termination and closing its last handle.
    Child outside; STARTUPINFOW si = {sizeof(si)};
    std::wstring outsideCommand = L"\"" + std::wstring(self) + L"\"";
    require(CreateProcessW(self, outsideCommand.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED,
        nullptr, nullptr, &si, &outside.pi), "outside witness");
    for (int occupied = 0; occupied < 2; ++occupied) {
        HANDLE job = CreateJobObjectW(nullptr, nullptr); require(job != nullptr, "job");
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = {};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        require(SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)), "job limits");
        Child hold; if (occupied) start(hold, job, self);
        std::wstring name = L"Local\\RegionLauncher.Test.JobProcess." + std::to_wstring(GetCurrentProcessId()) + std::to_wstring(occupied);
        HANDLE event = CreateEventW(nullptr, TRUE, FALSE, name.c_str()); require(event != nullptr, "event");
        Child target; start(target, job, self, L"--child " + name);
        require(WaitForSingleObject(event, 50) == WAIT_TIMEOUT, "application code did not run before resume");
        require(ResumeThread(target.pi.hThread) == 1, "primary thread was suspended");
        require(WaitForSingleObject(event, 5000) == WAIT_OBJECT_0, "application runs after resume");
        // A real packaged target is optional; never resume it in this regression fixture.
        Child packaged; if (argc == 2) start(packaged, job, argv[1]);
        if (!occupied) require(TerminateJobObject(job, 0), "terminate test group");
        CloseHandle(job);
        require(WaitForSingleObject(target.pi.hProcess, 5000) == WAIT_OBJECT_0, "target terminated with group");
        if (occupied) require(WaitForSingleObject(hold.pi.hProcess, 5000) == WAIT_OBJECT_0, "existing target terminated with group");
        if (argc == 2) require(WaitForSingleObject(packaged.pi.hProcess, 5000) == WAIT_OBJECT_0, "packaged target terminated with group");
        require(WaitForSingleObject(outside.pi.hProcess, 0) == WAIT_TIMEOUT, "outside process survived");
        CloseHandle(event);
    }
    // A caller without assignment rights must fail closed and create no runnable target.
    HANDLE job = CreateJobObjectW(nullptr, nullptr), queryOnly = nullptr;
    require(DuplicateHandle(GetCurrentProcess(), job, GetCurrentProcess(), &queryOnly, JOB_OBJECT_QUERY, FALSE, 0), "query-only handle");
    Child rejected; std::wstring command = L"\"" + std::wstring(self) + L"\"";
    require(!CreateSuspendedJobProcess(queryOnly, self, command.data(), nullptr, si, &rejected.pi), "reject unauthorized job");
    require(!rejected.pi.hProcess, "failed creation leaves no process");
    CloseHandle(queryOnly); CloseHandle(job);
    puts("PASS: atomic enrollment, empty/populated groups, suspended execution, job termination, outside survivor, denied assignment");
    return 0;
}
int wmain(int argc, WCHAR** argv) {
    try { return run(argc, argv); }
    catch (const std::exception& e) { fprintf(stderr, "FAIL: %s\n", e.what()); return 1; }
}
