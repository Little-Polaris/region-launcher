#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include <string>

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) return 2;
    if (!wcscmp(argv[1], L"--twice")) {
        if (argc < 4) return 2;
        auto first = (INT_PTR)ShellExecuteW(nullptr, L"open", argv[2], nullptr, nullptr, SW_SHOWNORMAL);
        Sleep(2000);
        auto second = (INT_PTR)ShellExecuteW(nullptr, L"open", argv[3], nullptr, nullptr, SW_SHOWNORMAL);
        return first > 32 && second > 32 ? 0 : 1;
    }
    if (!wcscmp(argv[1], L"--inside-owner")) {
        WNDCLASSW cls = {}; cls.lpfnWndProc = DefWindowProcW; cls.hInstance = GetModuleHandleW(nullptr); cls.lpszClassName = L"RegionLinkFixture";
        RegisterClassW(&cls);
        if (!CreateWindowW(cls.lpszClassName, argv[2], 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, cls.hInstance, nullptr)) return 3;
        argc -= 2; argv += 2;
    }
    if (!wcscmp(argv[1], L"--owner")) {
        WNDCLASSW cls = {}; cls.lpfnWndProc = DefWindowProcW; cls.hInstance = GetModuleHandleW(nullptr); cls.lpszClassName = L"RegionLinkFixture";
        RegisterClassW(&cls);
        HWND window = CreateWindowW(cls.lpszClassName, argv[2], 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, cls.hInstance, nullptr);
        if (!window) return 3;
        puts("OWNER_READY"); fflush(stdout);
        MSG message; while (GetMessageW(&message, nullptr, 0, 0) > 0) DispatchMessageW(&message);
        return 0;
    }
    if (wcsstr(argv[1], L"--user-data-dir=")) {
        WCHAR path[32768] = {}, locale[85] = {}, jobName[256] = {};
        GetEnvironmentVariableW(L"LINK_TEST_OUTPUT", path, _countof(path)); GetUserDefaultLocaleName(locale, _countof(locale));
        GetEnvironmentVariableW(L"REGION_LAUNCHER_JOB", jobName, _countof(jobName));
        HANDLE job = OpenJobObjectW(JOB_OBJECT_QUERY, FALSE, jobName); BOOL member = FALSE;
        if (job) { IsProcessInJob(GetCurrentProcess(), job, &member); CloseHandle(job); }
        FILE* file = nullptr; _wfopen_s(&file, path, L"w, ccs=UTF-8"); if (!file) return 4;
        fwprintf(file, L"locale=%s\ninside=%d\n", locale, member);
        for (int i = 1; i < argc; ++i) fwprintf(file, L"%s\n", argv[i]);
        fclose(file); return 0;
    }
    if (argc < 3) return 2;
    bool result = false; HANDLE process = nullptr;
    if (!wcscmp(argv[1], L"exw")) {
        SHELLEXECUTEINFOW info = {}; info.cbSize = sizeof(info); info.fMask = SEE_MASK_NOCLOSEPROCESS;
        info.lpVerb = L"open"; info.lpFile = argv[2]; info.nShow = SW_SHOWNORMAL;
        result = ShellExecuteExW(&info); process = info.hProcess;
    } else if (!wcscmp(argv[1], L"w")) {
        result = (INT_PTR)ShellExecuteW(nullptr, L"open", argv[2], nullptr, nullptr, SW_SHOWNORMAL) > 32;
    } else {
        char url[32768]; WideCharToMultiByte(CP_ACP, 0, argv[2], -1, url, sizeof(url), nullptr, nullptr);
        if (!wcscmp(argv[1], L"exa")) {
            SHELLEXECUTEINFOA info = {}; info.cbSize = sizeof(info); info.fMask = SEE_MASK_NOCLOSEPROCESS;
            info.lpVerb = "open"; info.lpFile = url; info.nShow = SW_SHOWNORMAL;
            result = ShellExecuteExA(&info); process = info.hProcess;
        } else result = (INT_PTR)ShellExecuteA(nullptr, "open", url, nullptr, nullptr, SW_SHOWNORMAL) > 32;
    }
    DWORD error = GetLastError();
    if (process) { WaitForSingleObject(process, 1200); CloseHandle(process); }
    printf("LINK_RESULT=%d error=%lu\n", result, error); fflush(stdout);
    return result ? 0 : 1;
}
