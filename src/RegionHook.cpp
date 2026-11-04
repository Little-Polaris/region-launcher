#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <vector>
#include "detours.h"

static WCHAR locale[LOCALE_NAME_MAX_LENGTH], region[16];
static LCID lcid;
static LANGID lang;
static GEOID geo;
static DYNAMIC_TIME_ZONE_INFORMATION zone;
static char dllPath[MAX_PATH];
static decltype(&GetLocaleInfoEx) realLocaleInfoEx = GetLocaleInfoEx;
static decltype(&GetLocaleInfoW) realLocaleInfoW = GetLocaleInfoW;
static decltype(&GetLocaleInfoA) realLocaleInfoA = GetLocaleInfoA;
static decltype(&GetUserDefaultLocaleName) realUserLocaleName = GetUserDefaultLocaleName;
static decltype(&GetSystemDefaultLocaleName) realSystemLocaleName = GetSystemDefaultLocaleName;
static decltype(&GetUserDefaultLCID) realUserLCID = GetUserDefaultLCID;
static decltype(&GetSystemDefaultLCID) realSystemLCID = GetSystemDefaultLCID;
static decltype(&GetThreadLocale) realThreadLCID = GetThreadLocale;
static decltype(&GetThreadUILanguage) realThreadUI = GetThreadUILanguage;
static decltype(&GetThreadPreferredUILanguages) realThreadLanguages = GetThreadPreferredUILanguages;
static decltype(&GetUserDefaultLangID) realUserLang = GetUserDefaultLangID;
static decltype(&GetSystemDefaultLangID) realSystemLang = GetSystemDefaultLangID;
static decltype(&GetUserDefaultUILanguage) realUserUI = GetUserDefaultUILanguage;
static decltype(&GetSystemDefaultUILanguage) realSystemUI = GetSystemDefaultUILanguage;
static decltype(&GetTimeZoneInformation) realZone = GetTimeZoneInformation;
static decltype(&GetDynamicTimeZoneInformation) realDynamicZone = GetDynamicTimeZoneInformation;
static decltype(&SystemTimeToTzSpecificLocalTimeEx) realUtcToLocal = SystemTimeToTzSpecificLocalTimeEx;
static decltype(&TzSpecificLocalTimeToSystemTimeEx) realLocalToUtc = TzSpecificLocalTimeToSystemTimeEx;
static decltype(&GetTimeZoneInformationForYear) realYearZone = GetTimeZoneInformationForYear;
static decltype(&GetLocalTime) realLocalTime = GetLocalTime;
static decltype(&GetUserGeoID) realGeo = GetUserGeoID;
static decltype(&GetUserDefaultGeoName) realGeoName = GetUserDefaultGeoName;
static decltype(&CreateProcessW) realCreateW = CreateProcessW;
static decltype(&CreateProcessA) realCreateA = CreateProcessA;
static decltype(&GetUserPreferredUILanguages) realUserLanguages = GetUserPreferredUILanguages;
static decltype(&GetSystemPreferredUILanguages) realSystemLanguages = GetSystemPreferredUILanguages;

static int copyString(const WCHAR* text, WCHAR* out, int size) {
    int need = (int)wcslen(text) + 1;
    if (!out && size == 0) return need;
    if (!out || size < need) { SetLastError(ERROR_INSUFFICIENT_BUFFER); return 0; }
    memcpy(out, text, need * sizeof(WCHAR)); return need;
}
static int WINAPI localeName(WCHAR* out, int size) { return copyString(locale, out, size); }
static LCID WINAPI getLCID() { return lcid; }
static LANGID WINAPI getLang() { return lang; }
static GEOID WINAPI getGeo(GEOCLASS type) { return type == GEOCLASS_NATION ? geo : realGeo(type); }
static int WINAPI getGeoName(WCHAR* out, int size) { return copyString(region, out, size); }
static LPCWSTR effectiveLocale(LPCWSTR input) {
    return !input || !wcscmp(input, LOCALE_NAME_SYSTEM_DEFAULT) ? locale : input;
}
static LCID effectiveLCID(LCID input) {
    return input == LOCALE_USER_DEFAULT || input == LOCALE_SYSTEM_DEFAULT || input == LOCALE_CUSTOM_DEFAULT ? lcid : input;
}
static int WINAPI localeInfoEx(LPCWSTR name, LCTYPE type, LPWSTR out, int size) {
    return realLocaleInfoEx(effectiveLocale(name), type, out, size);
}
static int WINAPI localeInfoW(LCID id, LCTYPE type, LPWSTR out, int size) {
    return realLocaleInfoW(effectiveLCID(id), type, out, size);
}
static int WINAPI localeInfoA(LCID id, LCTYPE type, LPSTR out, int size) {
    return realLocaleInfoA(effectiveLCID(id), type, out, size);
}
static BOOL WINAPI uiLanguages(DWORD flags, PULONG count, PZZWSTR out, PULONG size) {
    if (!count || !size) { SetLastError(ERROR_INVALID_PARAMETER); return FALSE; }
    WCHAR value[LOCALE_NAME_MAX_LENGTH] = {};
    if (flags & MUI_LANGUAGE_ID) swprintf_s(value, L"%04x", lang);
    else wcscpy_s(value, locale);
    ULONG needed = (ULONG)wcslen(value) + 2;
    *count = 1;
    if (!out && *size == 0) { *size = needed; return TRUE; }
    if (!out || *size < needed) { *size = needed; SetLastError(ERROR_INSUFFICIENT_BUFFER); return FALSE; }
    memcpy(out, value, (needed - 1) * sizeof(WCHAR)); out[needed - 1] = 0; *size = needed; return TRUE;
}
static DWORD season() {
    if (!zone.DaylightDate.wMonth) return TIME_ZONE_ID_UNKNOWN;
    SYSTEMTIME utc, local; GetSystemTime(&utc);
    if (!SystemTimeToTzSpecificLocalTimeEx(&zone, &utc, &local)) return TIME_ZONE_ID_INVALID;
    FILETIME uf, lf; SystemTimeToFileTime(&utc, &uf); SystemTimeToFileTime(&local, &lf);
    ULARGE_INTEGER u, l; u.LowPart = uf.dwLowDateTime; u.HighPart = uf.dwHighDateTime;
    l.LowPart = lf.dwLowDateTime; l.HighPart = lf.dwHighDateTime;
    LONGLONG bias = ((LONGLONG)u.QuadPart - (LONGLONG)l.QuadPart) / 600000000LL;
    return bias == zone.Bias + zone.DaylightBias ? TIME_ZONE_ID_DAYLIGHT : TIME_ZONE_ID_STANDARD;
}
static bool fixedZone(const DYNAMIC_TIME_ZONE_INFORMATION* input) {
    return zone.DynamicDaylightTimeDisabled && (!input || !_wcsicmp(input->TimeZoneKeyName, zone.TimeZoneKeyName));
}
static BOOL WINAPI utcToLocal(const DYNAMIC_TIME_ZONE_INFORMATION* input, const SYSTEMTIME* utc, LPSYSTEMTIME out) {
    if (!fixedZone(input)) return realUtcToLocal(input ? input : &zone, utc, out);
    TIME_ZONE_INFORMATION fixed = {}; fixed.Bias = zone.Bias; fixed.StandardBias = zone.StandardBias;
    return SystemTimeToTzSpecificLocalTime(&fixed, utc, out);
}
static BOOL WINAPI localToUtc(const DYNAMIC_TIME_ZONE_INFORMATION* input, const SYSTEMTIME* local, LPSYSTEMTIME out) {
    if (!fixedZone(input)) return realLocalToUtc(input ? input : &zone, local, out);
    TIME_ZONE_INFORMATION fixed = {}; fixed.Bias = zone.Bias; fixed.StandardBias = zone.StandardBias;
    return TzSpecificLocalTimeToSystemTime(&fixed, local, out);
}
static BOOL WINAPI yearZone(USHORT year, PDYNAMIC_TIME_ZONE_INFORMATION input, LPTIME_ZONE_INFORMATION out) {
    BOOL result = realYearZone(year, input ? input : &zone, out);
    if (result && fixedZone(input)) { out->DaylightDate = {}; out->StandardDate = {}; out->DaylightBias = 0; }
    return result;
}
static DWORD WINAPI dynamicZone(PDYNAMIC_TIME_ZONE_INFORMATION out) {
    if (!out) { SetLastError(ERROR_INVALID_PARAMETER); return TIME_ZONE_ID_INVALID; }
    *out = zone; return season();
}
static DWORD WINAPI timeZone(LPTIME_ZONE_INFORMATION out) {
    if (!out) { SetLastError(ERROR_INVALID_PARAMETER); return TIME_ZONE_ID_INVALID; }
    SYSTEMTIME now; GetSystemTime(&now);
    if (!GetTimeZoneInformationForYear(now.wYear, &zone, out)) return TIME_ZONE_ID_INVALID;
    if (zone.DynamicDaylightTimeDisabled) {
        out->DaylightDate = {}; out->StandardDate = {}; out->DaylightBias = 0;
    }
    return season();
}
static void WINAPI localTime(LPSYSTEMTIME out) {
    SYSTEMTIME utc; GetSystemTime(&utc);
    if (out) SystemTimeToTzSpecificLocalTimeEx(&zone, &utc, out);
}
// Child processes are instrumented before their first application instruction.
// shortcut: service/COM broker activation is outside this process tree and requires app-specific integration.
static BOOL WINAPI createW(LPCWSTR app, LPWSTR cmd, LPSECURITY_ATTRIBUTES pa, LPSECURITY_ATTRIBUTES ta,
    BOOL inherit, DWORD flags, LPVOID env, LPCWSTR cwd, LPSTARTUPINFOW si, LPPROCESS_INFORMATION pi) {
    return DetourCreateProcessWithDllExW(app, cmd, pa, ta, inherit, flags, env, cwd, si, pi, dllPath, realCreateW);
}
static BOOL WINAPI createA(LPCSTR app, LPSTR cmd, LPSECURITY_ATTRIBUTES pa, LPSECURITY_ATTRIBUTES ta,
    BOOL inherit, DWORD flags, LPVOID env, LPCSTR cwd, LPSTARTUPINFOA si, LPPROCESS_INFORMATION pi) {
    return DetourCreateProcessWithDllExA(app, cmd, pa, ta, inherit, flags, env, cwd, si, pi, dllPath, realCreateA);
}
extern "C" __declspec(dllexport) void RegionHookMarker() {}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (DetourIsHelperProcess()) return TRUE;
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DetourRestoreAfterWith();
    WCHAR tz[128] = {}, geoText[32] = {};
    if (!GetEnvironmentVariableW(L"REGION_LAUNCHER_LOCALE", locale, _countof(locale)) ||
        !GetEnvironmentVariableW(L"REGION_LAUNCHER_TIMEZONE", tz, _countof(tz)) ||
        !GetEnvironmentVariableW(L"REGION_LAUNCHER_REGION", region, _countof(region)) ||
        !GetEnvironmentVariableW(L"REGION_LAUNCHER_GEO", geoText, _countof(geoText))) return FALSE;
    lcid = LocaleNameToLCID(locale, 0); lang = LANGIDFROMLCID(lcid); geo = _wtoi(geoText);
    if (!lcid || !geo || !GetModuleFileNameA(instance, dllPath, _countof(dllPath))) return FALSE;
    bool found = false;
    for (DWORD i = 0; EnumDynamicTimeZoneInformation(i, &zone) == ERROR_SUCCESS; ++i) {
        if (!_wcsicmp(zone.TimeZoneKeyName, tz)) { found = true; break; }
    }
    if (!found) return FALSE;
    WCHAR dst[8] = {};
    GetEnvironmentVariableW(L"REGION_LAUNCHER_DST", dst, _countof(dst));
    zone.DynamicDaylightTimeDisabled = !wcscmp(dst, L"0");
    if (zone.DynamicDaylightTimeDisabled) {
        zone.DaylightDate = {}; zone.StandardDate = {}; zone.DaylightBias = 0;
    }
    if (DetourTransactionBegin() != NO_ERROR) return FALSE;
    DetourUpdateThread(GetCurrentThread());
    LONG status = NO_ERROR;
#define HOOK(old, replacement) if (DetourAttach(&(PVOID&)(old), replacement) != NO_ERROR) status = ERROR_INVALID_FUNCTION
    HOOK(realUserLocaleName, localeName); HOOK(realSystemLocaleName, localeName);
    HOOK(realUserLCID, getLCID); HOOK(realSystemLCID, getLCID);
    HOOK(realThreadLCID, getLCID); HOOK(realThreadUI, getLang); HOOK(realThreadLanguages, uiLanguages);
    HOOK(realUserLang, getLang); HOOK(realSystemLang, getLang);
    HOOK(realUserUI, getLang); HOOK(realSystemUI, getLang);
    HOOK(realLocaleInfoEx, localeInfoEx); HOOK(realLocaleInfoW, localeInfoW); HOOK(realLocaleInfoA, localeInfoA);
    HOOK(realZone, timeZone); HOOK(realDynamicZone, dynamicZone); HOOK(realLocalTime, localTime);
    HOOK(realUtcToLocal, utcToLocal); HOOK(realLocalToUtc, localToUtc); HOOK(realYearZone, yearZone);
    HOOK(realGeo, getGeo); HOOK(realGeoName, getGeoName);
    HOOK(realUserLanguages, uiLanguages); HOOK(realSystemLanguages, uiLanguages);
    HOOK(realCreateW, createW); HOOK(realCreateA, createA);
#undef HOOK
    if (status != NO_ERROR) { DetourTransactionAbort(); return FALSE; }
    return DetourTransactionCommit() == NO_ERROR;
}
