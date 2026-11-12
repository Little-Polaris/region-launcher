#pragma once
#include <windows.h>
#include <psapi.h>
#include <stddef.h>
#include <string>
#include <vector>

// The primary thread stays suspended. A matching x86/x64 loader calls LdrLoadDll
// with a UTF-16 UNICODE_STRING, rather than adding an ANSI PE import name.
// Loader initialization can run dependency initializers before our module.
inline bool InjectUnicodeDllLocal(HANDLE process, const std::wstring& path) {
    if (path.empty() || path.size() > 32766) { SetLastError(ERROR_FILENAME_EXCED_RANGE); return false; }
    USHORT machine = 0, native = 0;
    if (!IsWow64Process2(process, &machine, &native)) return false;
    if (native != IMAGE_FILE_MACHINE_AMD64 ||
#ifdef _WIN64
        machine != IMAGE_FILE_MACHINE_UNKNOWN
#else
        machine != IMAGE_FILE_MACHINE_I386
#endif
    ) { SetLastError(ERROR_BAD_EXE_FORMAT); return false; }
    HMODULE localNtdll = GetModuleHandleW(L"ntdll.dll");
    auto loader = GetProcAddress(localNtdll, "LdrLoadDll");
    if (!loader) return false;
    uintptr_t remoteNtdll = 0, cursor = 0;
    MEMORY_BASIC_INFORMATION memory = {};
    while (VirtualQueryEx(process, reinterpret_cast<void*>(cursor), &memory, sizeof(memory))) {
        if (memory.Type == MEM_IMAGE && memory.AllocationBase == memory.BaseAddress) {
            WCHAR name[32768] = {};
            DWORD n = K32GetMappedFileNameW(process, memory.AllocationBase, name, _countof(name));
            if (n > 10 && n < _countof(name) && !_wcsicmp(name + n - 10, L"\\ntdll.dll")) {
                remoteNtdll = reinterpret_cast<uintptr_t>(memory.AllocationBase); break;
            }
        }
        uintptr_t next = reinterpret_cast<uintptr_t>(memory.BaseAddress) + memory.RegionSize;
        if (next <= cursor) break;
        cursor = next;
    }
    if (!remoteNtdll) { SetLastError(ERROR_MOD_NOT_FOUND); return false; }
    struct Payload {
        uintptr_t loader;
        HMODULE module;
        USHORT length, maximumLength;
#ifdef _WIN64
        ULONG padding;
#endif
        uintptr_t buffer;
        LONG status;
        ULONG reserved;
    };
#ifdef _WIN64
    static_assert(offsetof(Payload, length) == 16 && offsetof(Payload, status) == 32 && sizeof(Payload) == 40);
    const BYTE stub[] = {
        0x48,0x83,0xec,0x28,             // sub rsp,28h (shadow space + alignment)
        0x49,0x89,0xca,                  // mov r10,rcx (payload)
        0x4c,0x89,0x54,0x24,0x20,       // mov [rsp+20h],r10
        0x31,0xc9,0x31,0xd2,            // xor ecx,ecx; xor edx,edx
        0x4d,0x8d,0x42,0x10,            // lea r8,[r10+10h] (UNICODE_STRING)
        0x4d,0x8d,0x4a,0x08,            // lea r9,[r10+8] (module output)
        0x49,0x8b,0x02,0xff,0xd0,       // mov rax,[r10]; call rax
        0x4c,0x8b,0x54,0x24,0x20,       // mov r10,[rsp+20h]
        0x41,0x89,0x42,0x20,            // mov [r10+20h],eax (NTSTATUS)
        0x48,0x83,0xc4,0x28,0xc3        // add rsp,28h; ret
    };
#else
    static_assert(offsetof(Payload, length) == 8 && offsetof(Payload, status) == 16 && sizeof(Payload) == 24);
    const BYTE stub[] = {
        0x53,                         // push ebx
        0x8b,0x5c,0x24,0x08,          // mov ebx,[esp+8] (payload)
        0x8d,0x43,0x04,0x50,          // push &module
        0x8d,0x43,0x08,0x50,          // push &UNICODE_STRING
        0x6a,0x00,0x6a,0x00,          // push flags, search path
        0xff,0x13,                    // call [ebx] (stdcall LdrLoadDll)
        0x89,0x43,0x10,               // save NTSTATUS
        0x5b,0xc2,0x04,0x00           // pop ebx; ret 4
    };
#endif
    SIZE_T bytes = sizeof(Payload) + (path.size() + 1) * sizeof(wchar_t);
    void* data = VirtualAllocEx(process, nullptr, bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    void* code = VirtualAllocEx(process, nullptr, sizeof(stub), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    HANDLE thread = nullptr;
    bool success = false;
    bool canFree = true;
    DWORD failure = ERROR_NOT_ENOUGH_MEMORY;
    if (data && code) {
        Payload payload = {};
        payload.loader = remoteNtdll + reinterpret_cast<uintptr_t>(loader) - reinterpret_cast<uintptr_t>(localNtdll);
        payload.length = static_cast<USHORT>(path.size() * sizeof(wchar_t));
        payload.maximumLength = payload.length + sizeof(wchar_t);
        payload.buffer = reinterpret_cast<uintptr_t>(data) + sizeof(payload);
        payload.status = static_cast<LONG>(0xC0000001);
        DWORD previous = 0;
        if (WriteProcessMemory(process, data, &payload, sizeof(payload), nullptr) &&
            WriteProcessMemory(process, reinterpret_cast<void*>(payload.buffer), path.c_str(), (path.size()+1)*sizeof(wchar_t), nullptr) &&
            WriteProcessMemory(process, code, stub, sizeof(stub), nullptr) &&
            VirtualProtectEx(process, code, sizeof(stub), PAGE_EXECUTE_READ, &previous) &&
            FlushInstructionCache(process, code, sizeof(stub))) {
            thread = CreateRemoteThread(process, nullptr, 0, reinterpret_cast<LPTHREAD_START_ROUTINE>(code), data, 0, nullptr);
            if (thread) {
                DWORD wait = WaitForSingleObject(thread, 15000);
                if (wait == WAIT_OBJECT_0 && ReadProcessMemory(process, data, &payload, sizeof(payload), nullptr)) {
                    success = payload.status >= 0 && payload.module != nullptr;
                    using StatusToError = ULONG (WINAPI*)(LONG);
                    auto convert = reinterpret_cast<StatusToError>(GetProcAddress(localNtdll, "RtlNtStatusToDosError"));
                    failure = success ? ERROR_SUCCESS : payload.status < 0 && convert ? convert(payload.status) : ERROR_DLL_INIT_FAILED;
                } else {
                    failure = wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError();
                    // Do not free code still executing in the remote process.
                    TerminateProcess(process, failure);
                    canFree = WaitForSingleObject(process, 5000) == WAIT_OBJECT_0 || WaitForSingleObject(thread, 0) == WAIT_OBJECT_0;
                }
            } else failure = GetLastError();
        } else failure = GetLastError();
    }
    if (thread) CloseHandle(thread);
    if (canFree && code) VirtualFreeEx(process, code, 0, MEM_RELEASE);
    if (canFree && data) VirtualFreeEx(process, data, 0, MEM_RELEASE);
    SetLastError(failure); return success;
}

inline bool InjectUnicodeDll(HANDLE process, const std::wstring& module,
    decltype(&CreateProcessW) create = CreateProcessW) {
    USHORT machine = 0, native = 0;
    if (!IsWow64Process2(process, &machine, &native)) return false;
    if (native != IMAGE_FILE_MACHINE_AMD64 || (machine != IMAGE_FILE_MACHINE_UNKNOWN && machine != IMAGE_FILE_MACHINE_I386)) {
        SetLastError(ERROR_BAD_EXE_FORMAT); return false;
    }
    bool target32 = machine == IMAGE_FILE_MACHINE_I386;
    auto directory = module.substr(0, module.find_last_of(L"\\/"));
    auto path = directory + (target32 ? L"\\RegionHook32.dll" : L"\\RegionHook.dll");
    if (target32 == (sizeof(void*) == 4)) return InjectUnicodeDllLocal(process, path);

    // Cross-bitness pointer lookup and loading run in a matching helper. Pass only
    // the already-open process handle; never reopen by a potentially reused PID.
    HANDLE inherited = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), process, GetCurrentProcess(), &inherited, 0, TRUE, DUPLICATE_SAME_ACCESS)) return false;
    SIZE_T size = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
    std::vector<BYTE> storage(size);
    STARTUPINFOEXW startup = {}; startup.StartupInfo.cb = sizeof(startup);
    startup.lpAttributeList = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
    bool initialized = InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, &size) != FALSE;
    bool success = false;
    DWORD failure = GetLastError();
    if (initialized && UpdateProcThreadAttribute(startup.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
        &inherited, sizeof(inherited), nullptr, nullptr)) {
        auto helper = directory + (target32 ? L"\\RegionInject32.exe" : L"\\RegionInject64.exe");
        auto command = L"\"" + helper + L"\" " + std::to_wstring(reinterpret_cast<uintptr_t>(inherited)) + L" \"" + path + L"\"";
        PROCESS_INFORMATION pi = {};
        // Use the original API inside RegionHook, so this helper is not injected recursively.
        if (create(helper.c_str(), command.data(), nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr, &startup.StartupInfo, &pi)) {
            DWORD wait = WaitForSingleObject(pi.hProcess, 30000);
            if (wait == WAIT_OBJECT_0) {
                if (GetExitCodeProcess(pi.hProcess, &failure)) success = failure == ERROR_SUCCESS;
                else failure = GetLastError();
            } else {
                failure = wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError();
                TerminateProcess(pi.hProcess, failure); WaitForSingleObject(pi.hProcess, 5000);
            }
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        } else failure = GetLastError();
    } else if (initialized) failure = GetLastError();
    if (initialized) DeleteProcThreadAttributeList(startup.lpAttributeList);
    CloseHandle(inherited);
    SetLastError(failure); return success;
}

inline BOOL FinishUnicodeChild(BOOL created, DWORD flags, LPPROCESS_INFORMATION child, const std::wstring& module,
    decltype(&CreateProcessW) create = CreateProcessW) {
    if (!created) return FALSE;
    if (InjectUnicodeDll(child->hProcess, module, create) && ((flags & CREATE_SUSPENDED) || ResumeThread(child->hThread) != DWORD(-1))) return TRUE;
    DWORD failure = GetLastError();
    TerminateProcess(child->hProcess, failure); WaitForSingleObject(child->hProcess, 5000);
    CloseHandle(child->hThread); CloseHandle(child->hProcess); *child = {};
    SetLastError(failure); return FALSE;
}
