#pragma once
#include <shellapi.h>
#include <stdio.h>

namespace LinkHooks {
static std::wstring browser, profile, temporary, profileName, jobName;
static decltype(&ShellExecuteExW) originalExW = ShellExecuteExW;
static decltype(&ShellExecuteExA) originalExA = ShellExecuteExA;
static decltype(&ShellExecuteW) originalW = ShellExecuteW;
static decltype(&ShellExecuteA) originalA = ShellExecuteA;
inline std::wstring env(const wchar_t* name) {
    DWORD n = GetEnvironmentVariableW(name, nullptr, 0);
    if (!n) return {};
    std::wstring value(n, 0); GetEnvironmentVariableW(name, value.data(), n); value.resize(wcslen(value.c_str())); return value;
}
inline bool web(const wchar_t* name) { return name && (!_wcsnicmp(name, L"https://", 8) || !_wcsnicmp(name, L"http://", 7)); }
inline std::wstring quote(const std::wstring& value) {
    std::wstring out = L"\""; size_t slashes = 0;
    for (auto c : value) {
        if (c == L'\\') { ++slashes; continue; }
        out.append(slashes * (c == L'\"' ? 2 : 1), L'\\'); slashes = 0;
        if (c == L'\"') out += L'\\'; out += c;
    }
    out.append(slashes * 2, L'\\'); return out + L'\"';
}
inline std::wstring wide(const char* text) {
    if (!text) return {};
    int n = MultiByteToWideChar(CP_ACP, MB_ERR_INVALID_CHARS, text, -1, nullptr, 0);
    if (!n) throw std::runtime_error("Invalid ANSI link");
    std::wstring result(n, 0); MultiByteToWideChar(CP_ACP, 0, text, -1, result.data(), n); result.pop_back(); return result;
}
// Chromium's singleton message window is titled with its full user-data directory.
// Reuse is permitted only when its owner is already in this exact job.
inline bool outsideOwner(const std::wstring& directory, HANDLE job) {
    HWND previous = nullptr;
    while ((previous = FindWindowExW(HWND_MESSAGE, previous, nullptr, directory.c_str()))) {
        DWORD pid = 0; GetWindowThreadProcessId(previous, &pid);
        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        BOOL inside = FALSE;
        bool ok = process && IsProcessInJob(process, job, &inside);
        if (process) CloseHandle(process);
        if (!ok || !inside) return true;
    }
    return false;
}
inline BOOL open(SHELLEXECUTEINFOW* info) {
    info->hProcess = nullptr; info->hInstApp = (HINSTANCE)SE_ERR_ACCESSDENIED;
    if ((info->lpVerb && _wcsicmp(info->lpVerb, L"open") && _wcsicmp(info->lpVerb, L"opennew")) ||
        (info->lpParameters && *info->lpParameters) || !web(info->lpFile)) { SetLastError(ERROR_INVALID_PARAMETER); return FALSE; }
    if (browser.empty() || temporary.empty()) { SetLastError(ERROR_FILE_NOT_FOUND); return FALSE; }
    // Serialize launcher-originated starts until Chromium publishes its singleton window.
    struct LaunchLock {
        HANDLE handle = CreateMutexW(nullptr, FALSE, L"Local\\RegionLauncher.LinkBrowser.Start");
        bool owned = false;
        ~LaunchLock() { if (owned) ReleaseMutex(handle); if (handle) CloseHandle(handle); }
    } lock;
    DWORD waited = lock.handle ? WaitForSingleObject(lock.handle, 15000) : WAIT_FAILED;
    lock.owned = waited == WAIT_OBJECT_0 || waited == WAIT_ABANDONED;
    if (!lock.owned) { SetLastError(ERROR_TIMEOUT); return FALSE; }
    HANDLE job = OpenJobObjectW(JOB_OBJECT_QUERY, FALSE, jobName.c_str());
    BOOL inside = FALSE;
    if (!job || !IsProcessInJob(GetCurrentProcess(), job, &inside) || !inside) {
        if (job) CloseHandle(job); SetLastError(ERROR_ACCESS_DENIED); return FALSE;
    }
    bool privateWindow = profile.empty() || outsideOwner(profile, job);
    const auto& directory = privateWindow ? temporary : profile;
    if (outsideOwner(directory, job)) { CloseHandle(job); SetLastError(ERROR_SHARING_VIOLATION); return FALSE; }
    std::wstring command = quote(browser) + L" " + quote(L"--user-data-dir=" + directory) + L" --no-first-run --no-default-browser-check";
    if (privateWindow) {
        auto basename = browser.substr(browser.find_last_of(L"\\/") + 1);
        command += !_wcsicmp(basename.c_str(), L"msedge.exe") ? L" --inprivate" : L" --incognito";
    } else if (!profileName.empty()) command += L" " + quote(L"--profile-directory=" + profileName);
    command += L" " + quote(info->lpFile);
    STARTUPINFOW si = {sizeof(si)}; si.dwFlags = STARTF_USESHOWWINDOW; si.wShowWindow = SW_SHOWNORMAL;
    PROCESS_INFORMATION pi = {};
    auto cwd = browser.substr(0, browser.find_last_of(L"\\/"));
    // The common CreateProcess hook injects before resume and retains the caller's launch context.
    BOOL created = CreateProcessW(browser.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED,
        nullptr, cwd.c_str(), &si, &pi);
    DWORD error = GetLastError(); inside = FALSE;
    if (created && (!IsProcessInJob(pi.hProcess, job, &inside) || !inside || ResumeThread(pi.hThread) == (DWORD)-1)) {
        error = ERROR_ACCESS_DENIED; TerminateProcess(pi.hProcess, error); CloseHandle(pi.hProcess); created = FALSE;
    }
    CloseHandle(job);
    if (pi.hThread) CloseHandle(pi.hThread);
    if (!created) { SetLastError(error); return FALSE; }
    for (int i = 0; i < 100 && !FindWindowExW(HWND_MESSAGE, nullptr, nullptr, directory.c_str()); ++i)
        if (WaitForSingleObject(pi.hProcess, 100) != WAIT_TIMEOUT) break;
    fprintf(stderr, "[LinkBrowser] scoped browser PID=%lu mode=%s\n", pi.dwProcessId, privateWindow ? "temporary-private" : "saved-profile");
    fflush(stderr); // Never log authorization URLs or their query strings.
    info->hInstApp = (HINSTANCE)33;
    if (info->fMask & SEE_MASK_NOCLOSEPROCESS) info->hProcess = pi.hProcess; else CloseHandle(pi.hProcess);
    return TRUE;
}
inline BOOL WINAPI executeExW(SHELLEXECUTEINFOW* info) {
    if (!info || info->cbSize != sizeof(*info) || !web(info->lpFile)) return originalExW(info);
    try { if (open(info)) return TRUE; }
    catch (...) { SetLastError(ERROR_NOT_ENOUGH_MEMORY); }
    DWORD error = GetLastError(); fprintf(stderr, "[LinkBrowser] open failed Windows error=%lu; no external fallback\n", error); fflush(stderr);
    SetLastError(error); return FALSE;
}
inline BOOL WINAPI executeExA(SHELLEXECUTEINFOA* info) {
    if (!info || info->cbSize != sizeof(*info)) return originalExA(info);
    try {
        auto file = wide(info->lpFile); if (!web(file.c_str())) return originalExA(info);
        auto verb = wide(info->lpVerb), parameters = wide(info->lpParameters);
        SHELLEXECUTEINFOW converted = {}; converted.cbSize = sizeof(converted); converted.fMask = info->fMask;
        converted.lpFile = file.c_str(); converted.lpVerb = verb.empty() ? nullptr : verb.c_str(); converted.lpParameters = parameters.c_str();
        BOOL result = executeExW(&converted); info->hProcess = converted.hProcess; info->hInstApp = converted.hInstApp; return result;
    } catch (...) { SetLastError(ERROR_INVALID_PARAMETER); return FALSE; }
}
inline HINSTANCE WINAPI executeW(HWND window, LPCWSTR verb, LPCWSTR file, LPCWSTR parameters, LPCWSTR cwd, INT show) {
    if (!web(file)) return originalW(window, verb, file, parameters, cwd, show);
    SHELLEXECUTEINFOW info = {}; info.cbSize = sizeof(info); info.lpVerb = verb; info.lpFile = file; info.lpParameters = parameters;
    executeExW(&info); return info.hInstApp;
}
inline HINSTANCE WINAPI executeA(HWND window, LPCSTR verb, LPCSTR file, LPCSTR parameters, LPCSTR cwd, INT show) {
    try {
        auto target = wide(file); if (!web(target.c_str())) return originalA(window, verb, file, parameters, cwd, show);
        auto action = wide(verb), args = wide(parameters);
        return executeW(window, action.empty() ? nullptr : action.c_str(), target.c_str(), args.c_str(), nullptr, show);
    } catch (...) { SetLastError(ERROR_INVALID_PARAMETER); return (HINSTANCE)SE_ERR_ACCESSDENIED; }
}
inline bool attach() {
    browser = env(L"REGION_LAUNCHER_LINK_EXE"); profile = env(L"REGION_LAUNCHER_LINK_PROFILE");
    temporary = env(L"REGION_LAUNCHER_LINK_TEMP"); profileName = env(L"REGION_LAUNCHER_LINK_PROFILE_NAME"); jobName = env(L"REGION_LAUNCHER_JOB");
    return DetourAttach(&(PVOID&)originalExW, executeExW) == NO_ERROR && DetourAttach(&(PVOID&)originalExA, executeExA) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalW, executeW) == NO_ERROR && DetourAttach(&(PVOID&)originalA, executeA) == NO_ERROR;
}
}
