#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winhttp.h>
#include "detours.h"
#include "SystemProxy.h"

static decltype(&WinHttpGetIEProxyConfigForCurrentUser) realUserProxy = WinHttpGetIEProxyConfigForCurrentUser;
static decltype(&WinHttpGetDefaultProxyConfiguration) realDefaultProxy = WinHttpGetDefaultProxyConfiguration;
static decltype(&WinHttpOpen) realOpen = WinHttpOpen;

// The packet engine owns egress. Inheriting the host's local proxy would hand
// traffic to a process outside the selected job and reintroduce host routing.
static BOOL WINAPI userProxy(WINHTTP_CURRENT_USER_IE_PROXY_CONFIG* out) {
    if (!out) { SetLastError(ERROR_INVALID_PARAMETER); return FALSE; }
    *out = {}; return TRUE;
}
static BOOL WINAPI defaultProxy(WINHTTP_PROXY_INFO* out) {
    if (!out) { SetLastError(ERROR_INVALID_PARAMETER); return FALSE; }
    *out = {}; out->dwAccessType = WINHTTP_ACCESS_TYPE_NO_PROXY; return TRUE;
}
static HINTERNET WINAPI openSession(LPCWSTR agent, DWORD access, LPCWSTR proxy, LPCWSTR bypass, DWORD flags) {
    if (access == WINHTTP_ACCESS_TYPE_DEFAULT_PROXY || access == WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY) {
        access = WINHTTP_ACCESS_TYPE_NO_PROXY; proxy = nullptr; bypass = nullptr;
    }
    return realOpen(agent, access, proxy, bypass, flags);
}
bool AttachSystemProxyHooks() {
    return DetourAttach(&(PVOID&)realUserProxy, userProxy) == NO_ERROR &&
        DetourAttach(&(PVOID&)realDefaultProxy, defaultProxy) == NO_ERROR &&
        DetourAttach(&(PVOID&)realOpen, openSession) == NO_ERROR && AttachWinInetProxyHooks();
}
