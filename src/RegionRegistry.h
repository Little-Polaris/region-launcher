#pragma once
#include <windows.h>
#include <winternl.h>
#include <string>
#include <vector>
#include <cstring>
#include <memory>
#include "detours.h"

// Read-only overlays in this process. No registry key or value is written.
namespace RegionRegistry {
using Query = NTSTATUS (NTAPI*)(HANDLE, PUNICODE_STRING, int, PVOID, ULONG, PULONG);
using Enumerate = NTSTATUS (NTAPI*)(HANDLE, ULONG, int, PVOID, ULONG, PULONG);
using QueryKey = NTSTATUS (NTAPI*)(HANDLE, int, PVOID, ULONG, PULONG);
inline Query realQuery;
inline Enumerate realEnumerate;
inline QueryKey queryKey;
constexpr NTSTATUS small = (NTSTATUS)0xc0000023, overflow = (NTSTATUS)0x80000005;
struct Value { std::wstring name; DWORD type; std::vector<BYTE> bytes; bool activeBias = false; };
struct Key { std::wstring path; std::vector<Value> values; };
struct State { std::vector<Key> keys; DYNAMIC_TIME_ZONE_INFORMATION zone; };
inline const State* state;
inline thread_local bool reading;
struct Guard { Guard() { reading = true; } ~Guard() { reading = false; } };

inline std::wstring path(HANDLE handle) {
    struct { ULONG length; WCHAR name[2048]; } info = {};
    ULONG needed = 0;
    if (queryKey(handle, 3, &info, sizeof(info), &needed) < 0 || info.length > sizeof(info.name)) return {};
    return {info.name, info.length / sizeof(WCHAR)};
}
inline Key key(HKEY root, const WCHAR* subkey) {
    HKEY handle = nullptr;
    if (RegOpenKeyExW(root, subkey, 0, KEY_READ, &handle) != ERROR_SUCCESS) return {};
    auto name = path(handle); RegCloseKey(handle); return {name, {}};
}
inline void binary(Key& key, const WCHAR* name, DWORD type, const void* data, size_t size, bool active = false) {
    const auto* begin = static_cast<const BYTE*>(data);
    key.values.push_back({name, type, {begin, begin + size}, active});
}
inline void text(Key& key, const WCHAR* name, const std::wstring& value, DWORD type = REG_SZ) {
    binary(key, name, type, value.c_str(), (value.size() + 1) * sizeof(WCHAR));
}
inline void number(Key& key, const WCHAR* name, DWORD value, bool active = false) { binary(key, name, REG_DWORD, &value, sizeof(value), active); }
inline const Key* findKey(HANDLE handle) {
    auto name = path(handle);
    for (const auto& key : state->keys) if (!key.path.empty() && !_wcsicmp(key.path.c_str(), name.c_str())) return &key;
    return nullptr;
}
inline const Value* findValue(const Key& key, const std::wstring& name) {
    for (const auto& value : key.values) if (!_wcsicmp(value.name.c_str(), name.c_str())) return &value;
    return nullptr;
}
inline DWORD currentBias() {
    const auto& zone = state->zone;
    if (zone.DynamicDaylightTimeDisabled) return zone.Bias + zone.StandardBias;
    SYSTEMTIME utc, local; GetSystemTime(&utc);
    if (!SystemTimeToTzSpecificLocalTimeEx(&zone, &utc, &local)) return zone.Bias + zone.StandardBias;
    FILETIME uf, lf; SystemTimeToFileTime(&utc, &uf); SystemTimeToFileTime(&local, &lf);
    ULARGE_INTEGER u = {}, l = {}; u.LowPart = uf.dwLowDateTime; u.HighPart = uf.dwHighDateTime; l.LowPart = lf.dwLowDateTime; l.HighPart = lf.dwHighDateTime;
    return (DWORD)(((LONGLONG)u.QuadPart - (LONGLONG)l.QuadPart) / 600000000LL);
}
inline NTSTATUS pack(const Value& value, const std::wstring& name, int kind, void* output, ULONG capacity, ULONG* needed) {
    ULONG nameBytes = (ULONG)name.size() * sizeof(WCHAR), dataBytes = (ULONG)value.bytes.size();
    ULONG header = kind == 0 || kind == 2 ? 12 : kind == 4 ? 8 : 20;
    ULONG offset = kind == 1 || kind == 3 ? (header + nameBytes + (kind == 3 ? 7 : 3)) & ~(kind == 3 ? 7u : 3u) : header;
    ULONG size = kind == 0 ? header + nameBytes : offset + dataBytes;
    *needed = size;
    if (capacity < header) return small;
    std::vector<BYTE> result(size, 0);
    auto put = [&](ULONG at, ULONG number) { memcpy(result.data() + at, &number, sizeof(number)); };
    put(kind == 4 ? 0 : 4, value.type);
    if (kind == 0) { put(8, nameBytes); memcpy(result.data() + 12, name.data(), nameBytes); }
    else {
        if (kind == 1 || kind == 3) { put(8, offset); put(12, dataBytes); put(16, nameBytes); memcpy(result.data() + 20, name.data(), nameBytes); }
        else put(kind == 4 ? 4 : 8, dataBytes);
        memcpy(result.data() + offset, value.bytes.data(), dataBytes);
        if (value.activeBias) { DWORD bias = currentBias(); memcpy(result.data() + offset, &bias, sizeof(bias)); }
    }
    memcpy(output, result.data(), (std::min)(capacity, size));
    return capacity < size ? overflow : 0;
}
inline bool replaceable(NTSTATUS status, int kind) { return (status >= 0 || status == small || status == overflow) && kind >= 0 && kind <= 4; }
inline NTSTATUS NTAPI query(HANDLE handle, PUNICODE_STRING name, int kind, PVOID output, ULONG size, PULONG needed) {
    NTSTATUS result = realQuery(handle, name, kind, output, size, needed);
    if (reading || !replaceable(result, kind) || !name || !needed) return result;
    Guard guard;
    try {
        auto key = findKey(handle); if (!key) return result;
        std::wstring label(name->Buffer, name->Length / sizeof(WCHAR));
        auto value = findValue(*key, label); if (!value) return result;
        return pack(*value, label, kind, output, size, needed);
    } catch (...) { return (NTSTATUS)0xc0000017; }
}
inline NTSTATUS NTAPI enumerate(HANDLE handle, ULONG index, int kind, PVOID output, ULONG size, PULONG needed) {
    NTSTATUS result = realEnumerate(handle, index, kind, output, size, needed);
    if (reading || !replaceable(result, kind) || !needed) return result;
    Guard guard;
    try {
        auto key = findKey(handle); if (!key) return result;
        struct { ULONG title, type, length; WCHAR name[256]; } basic = {};
        ULONG length = 0;
        if (realEnumerate(handle, index, 0, &basic, sizeof(basic), &length) < 0 || basic.length > sizeof(basic.name)) return result;
        std::wstring label(basic.name, basic.length / sizeof(WCHAR));
        auto value = findValue(*key, label); if (!value) return result;
        return pack(*value, label, kind, output, size, needed);
    } catch (...) { return (NTSTATUS)0xc0000017; }
}
inline bool initialize(const WCHAR* locale, GEOID geo, const WCHAR* region, const DYNAMIC_TIME_ZONE_INFORMATION& zone) {
    auto ntdll = GetModuleHandleW(L"ntdll.dll");
    realQuery = (Query)GetProcAddress(ntdll, "NtQueryValueKey");
    realEnumerate = (Enumerate)GetProcAddress(ntdll, "NtEnumerateValueKey");
    queryKey = (QueryKey)GetProcAddress(ntdll, "NtQueryKey");
    if (!realQuery || !realEnumerate || !queryKey) return false;
    try {
        auto snapshot = std::make_unique<State>(); snapshot->zone = zone;
        auto intl = key(HKEY_CURRENT_USER, L"Control Panel\\International");
        if (intl.path.empty()) return false;
        WCHAR id[16]; swprintf_s(id, L"%08x", LocaleNameToLCID(locale, 0));
        text(intl, L"Locale", id); text(intl, L"LocaleName", locale);
        const struct { const WCHAR* name; LCTYPE type; } formats[] = {
            {L"sCountry", LOCALE_SCOUNTRY}, {L"iCountry", LOCALE_ICOUNTRY}, {L"sLanguage", LOCALE_SABBREVLANGNAME},
            {L"sShortDate", LOCALE_SSHORTDATE}, {L"sLongDate", LOCALE_SLONGDATE}, {L"sDate", LOCALE_SDATE},
            {L"sTimeFormat", LOCALE_STIMEFORMAT}, {L"sShortTime", LOCALE_SSHORTTIME}, {L"sTime", LOCALE_STIME},
            {L"s1159", LOCALE_S1159}, {L"s2359", LOCALE_S2359}, {L"iTime", LOCALE_ITIME}, {L"iTLZero", LOCALE_ITLZERO},
            {L"iDate", LOCALE_IDATE}, {L"iLDate", LOCALE_ILDATE}, {L"iFirstDayOfWeek", LOCALE_IFIRSTDAYOFWEEK},
            {L"iFirstWeekOfYear", LOCALE_IFIRSTWEEKOFYEAR}, {L"iCalendarType", LOCALE_ICALENDARTYPE},
            {L"sDecimal", LOCALE_SDECIMAL}, {L"sThousand", LOCALE_STHOUSAND}, {L"sGrouping", LOCALE_SGROUPING},
            {L"iDigits", LOCALE_IDIGITS}, {L"iLZero", LOCALE_ILZERO}, {L"sList", LOCALE_SLIST},
            {L"sCurrency", LOCALE_SCURRENCY}, {L"sMonDecimalSep", LOCALE_SMONDECIMALSEP},
            {L"sMonThousandSep", LOCALE_SMONTHOUSANDSEP}, {L"sMonGrouping", LOCALE_SMONGROUPING},
            {L"iCurrDigits", LOCALE_ICURRDIGITS}, {L"iCurrency", LOCALE_ICURRENCY}, {L"iNegCurr", LOCALE_INEGCURR},
            {L"iMeasure", LOCALE_IMEASURE}, {L"sNegativeSign", LOCALE_SNEGATIVESIGN}, {L"sPositiveSign", LOCALE_SPOSITIVESIGN}
        };
        for (const auto& format : formats) { WCHAR value[256]; if (GetLocaleInfoEx(locale, format.type | LOCALE_NOUSEROVERRIDE, value, _countof(value))) text(intl, format.name, value); }
        snapshot->keys.push_back(std::move(intl));
        auto geography = key(HKEY_CURRENT_USER, L"Control Panel\\International\\Geo");
        text(geography, L"Nation", std::to_wstring(geo)); text(geography, L"Name", region); snapshot->keys.push_back(std::move(geography));
        std::wstring languages(locale); languages.push_back(0);
        for (auto subkey : {L"Control Panel\\International\\User Profile", L"Control Panel\\Desktop", L"Control Panel\\Desktop\\MuiCached"}) {
            auto language = key(HKEY_CURRENT_USER, subkey);
            text(language, L"Languages", languages, REG_MULTI_SZ);
            text(language, L"PreferredUILanguages", languages, REG_MULTI_SZ);
            text(language, L"MachinePreferredUILanguages", languages, REG_MULTI_SZ);
            snapshot->keys.push_back(std::move(language));
        }
        auto systemLanguage = key(HKEY_LOCAL_MACHINE, L"SYSTEM\\CurrentControlSet\\Control\\Nls\\Language");
        swprintf_s(id, L"%04x", LANGIDFROMLCID(LocaleNameToLCID(locale, 0)));
        text(systemLanguage, L"Default", id); snapshot->keys.push_back(std::move(systemLanguage));
        auto timezone = key(HKEY_LOCAL_MACHINE, L"SYSTEM\\CurrentControlSet\\Control\\TimeZoneInformation");
        if (timezone.path.empty()) return false;
        text(timezone, L"TimeZoneKeyName", zone.TimeZoneKeyName);
        text(timezone, L"StandardName", zone.StandardName); text(timezone, L"DaylightName", zone.DaylightName);
        number(timezone, L"Bias", zone.Bias); number(timezone, L"StandardBias", zone.StandardBias); number(timezone, L"DaylightBias", zone.DaylightBias);
        number(timezone, L"ActiveTimeBias", zone.Bias, true);
        number(timezone, L"DynamicDaylightTimeDisabled", zone.DynamicDaylightTimeDisabled ? 1 : 0);
        binary(timezone, L"StandardStart", REG_BINARY, &zone.StandardDate, sizeof(SYSTEMTIME));
        binary(timezone, L"DaylightStart", REG_BINARY, &zone.DaylightDate, sizeof(SYSTEMTIME));
        snapshot->keys.push_back(std::move(timezone));
        state = snapshot.release(); return true;
    } catch (...) { return false; }
}
inline bool attach() {
    return DetourAttach(&(PVOID&)realQuery, query) == NO_ERROR && DetourAttach(&(PVOID&)realEnumerate, enumerate) == NO_ERROR;
}
}
