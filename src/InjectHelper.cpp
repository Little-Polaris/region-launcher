#define WIN32_LEAN_AND_MEAN
#include "UnicodeInjection.h"

int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return ERROR_INVALID_PARAMETER;
    wchar_t* end = nullptr;
    auto value = _wcstoui64(argv[1], &end, 10);
    if (!value || !end || *end || value > UINTPTR_MAX) return ERROR_INVALID_HANDLE;
    HANDLE process = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(value));
    if (!GetProcessId(process)) return GetLastError();
    bool loaded = InjectUnicodeDllLocal(process, argv[2]);
    DWORD error = loaded ? ERROR_SUCCESS : GetLastError();
    CloseHandle(process);
    return error ? error : loaded ? ERROR_SUCCESS : ERROR_DLL_INIT_FAILED;
}
