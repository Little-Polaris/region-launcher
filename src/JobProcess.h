#pragma once
#include <windows.h>
#include <vector>

// Enroll before Windows adds a packaged application's own job. Appending our
// populated job afterwards cannot form a valid nested hierarchy (error 5).
inline BOOL CreateSuspendedJobProcess(HANDLE job, const WCHAR* application,
    WCHAR* command, const WCHAR* directory, const STARTUPINFOW& startup,
    PROCESS_INFORMATION* process) {
    SIZE_T size = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
    if (!size) return FALSE;
    std::vector<BYTE> storage(size);
    STARTUPINFOEXW extended = {};
    extended.StartupInfo = startup;
    extended.StartupInfo.cb = sizeof(extended);
    extended.lpAttributeList = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
    if (!InitializeProcThreadAttributeList(extended.lpAttributeList, 1, 0, &size)) return FALSE;
    BOOL created = FALSE;
    if (UpdateProcThreadAttribute(extended.lpAttributeList, 0,
        PROC_THREAD_ATTRIBUTE_JOB_LIST, &job, sizeof(job), nullptr, nullptr)) {
        created = CreateProcessW(application, command, nullptr, nullptr, TRUE,
            CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT,
            nullptr, directory, &extended.StartupInfo, process);
    }
    DWORD failure = GetLastError();
    DeleteProcThreadAttributeList(extended.lpAttributeList);
    SetLastError(failure);
    return created;
}
