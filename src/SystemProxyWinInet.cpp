#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wininet.h>
#include "detours.h"
#include "SystemProxy.h"

static decltype(&InternetOpenW) realOpenW = InternetOpenW;
static decltype(&InternetOpenA) realOpenA = InternetOpenA;
static bool inherited(DWORD access) {
    return access == INTERNET_OPEN_TYPE_PRECONFIG || access == INTERNET_OPEN_TYPE_PRECONFIG_WITH_NO_AUTOPROXY;
}
static HINTERNET WINAPI openW(LPCWSTR agent, DWORD access, LPCWSTR proxy, LPCWSTR bypass, DWORD flags) {
    if (inherited(access)) { access = INTERNET_OPEN_TYPE_DIRECT; proxy = nullptr; bypass = nullptr; }
    return realOpenW(agent, access, proxy, bypass, flags);
}
static HINTERNET WINAPI openA(LPCSTR agent, DWORD access, LPCSTR proxy, LPCSTR bypass, DWORD flags) {
    if (inherited(access)) { access = INTERNET_OPEN_TYPE_DIRECT; proxy = nullptr; bypass = nullptr; }
    return realOpenA(agent, access, proxy, bypass, flags);
}
bool AttachWinInetProxyHooks() {
    return DetourAttach(&(PVOID&)realOpenW, openW) == NO_ERROR &&
        DetourAttach(&(PVOID&)realOpenA, openA) == NO_ERROR;
}
