#pragma once
#include <windows.h>
#include <psapi.h>
#include <stddef.h>
#include <string>

// The primary thread stays suspended. A small x64 loader calls LdrLoadDll
// with a UTF-16 UNICODE_STRING, rather than adding an ANSI PE import name.
// Loader initialization can run dependency initializers before our module.
inline bool InjectUnicodeDll(HANDLE process, const std::wstring& path) {
    if (path.empty() || path.size() > 32766) { SetLastError(ERROR_FILENAME_EXCED_RANGE); return false; }
    USHORT machine = 0, native = 0;
    if (!IsWow64Process2(process, &machine, &native)) return false;
    if (machine != IMAGE_FILE_MACHINE_UNKNOWN || native != IMAGE_FILE_MACHINE_AMD64) { SetLastError(ERROR_BAD_EXE_FORMAT); return false; }
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
        ULONG padding;
        uintptr_t buffer;
        LONG status;
        ULONG reserved;
    };
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

inline BOOL FinishUnicodeChild(BOOL created, DWORD flags, LPPROCESS_INFORMATION child, const std::wstring& module) {
    if (!created) return FALSE;
    if (InjectUnicodeDll(child->hProcess, module) && ((flags & CREATE_SUSPENDED) || ResumeThread(child->hThread) != DWORD(-1))) return TRUE;
    DWORD failure = GetLastError();
    TerminateProcess(child->hProcess, failure); WaitForSingleObject(child->hProcess, 5000);
    CloseHandle(child->hThread); CloseHandle(child->hProcess); *child = {};
    SetLastError(failure); return FALSE;
}
