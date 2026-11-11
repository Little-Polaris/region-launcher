#pragma once
#include <windows.h>
#include <map>
#include <string>
#include <vector>
#include <stdexcept>

namespace ChildEnvironment {
struct Less {
    bool operator()(const std::wstring& a, const std::wstring& b) const {
        return CompareStringOrdinal(a.c_str(), -1, b.c_str(), -1, TRUE) == CSTR_LESS_THAN;
    }
};
using Values = std::map<std::wstring, std::wstring, Less>;
inline const wchar_t* const owned[] = {
    L"REGION_LAUNCHER_LOCALE", L"REGION_LAUNCHER_REGION", L"REGION_LAUNCHER_GEO",
    L"REGION_LAUNCHER_TIMEZONE", L"REGION_LAUNCHER_DST", L"REGION_LAUNCHER_NETWORK",
    L"REGION_LAUNCHER_CONNECT_PIPE", L"REGION_LAUNCHER_DNS_ADDRESS", L"TZ", L"LANG", L"LC_ALL",
    L"REGION_LAUNCHER_JOB", L"REGION_LAUNCHER_LINK_ENABLED", L"REGION_LAUNCHER_LINK_EXE",
    L"REGION_LAUNCHER_LINK_PROFILE", L"REGION_LAUNCHER_LINK_TEMP", L"REGION_LAUNCHER_LINK_PROFILE_NAME"
};
inline Values capture() {
    Values result;
    for (auto name : owned) {
        DWORD size = GetEnvironmentVariableW(name, nullptr, 0);
        if (!size) continue;
        std::vector<wchar_t> value(size);
        DWORD written = GetEnvironmentVariableW(name, value.data(), size);
        if (!written || written >= size) throw std::runtime_error("Environment changed during initialization");
        result[name] = value.data();
    }
    return result;
}
inline void add(Values& values, const std::wstring& entry) {
    // Preserve Windows' hidden per-drive current-directory entries (=C:=...).
    auto equal = entry.find(L'=', 1);
    if (equal != std::wstring::npos) values[entry.substr(0, equal)] = entry.substr(equal + 1);
}
inline std::vector<wchar_t> build(void* input, DWORD flags, const Values& inherited) {
    Values values;
    if (!input) {
        auto block = GetEnvironmentStringsW();
        if (!block) throw std::bad_alloc();
        try { for (auto p = block; *p; p += wcslen(p) + 1) add(values, p); }
        catch (...) { FreeEnvironmentStringsW(block); throw; }
        FreeEnvironmentStringsW(block);
    } else if (flags & CREATE_UNICODE_ENVIRONMENT) {
        for (auto p = static_cast<const wchar_t*>(input); *p; p += wcslen(p) + 1) add(values, p);
    } else {
        for (auto p = static_cast<const char*>(input); *p; p += strlen(p) + 1) {
            int count = MultiByteToWideChar(CP_ACP, 0, p, -1, nullptr, 0);
            if (!count) throw std::runtime_error("Invalid ANSI environment");
            std::vector<wchar_t> text(count);
            if (!MultiByteToWideChar(CP_ACP, 0, p, -1, text.data(), count)) throw std::runtime_error("Invalid ANSI environment");
            add(values, text.data());
        }
    }
    // A replacement environment must retain this launch's routing and region,
    // without copying unrelated parent credentials or settings into the child.
    for (auto name : owned) values.erase(name);
    values.insert(inherited.begin(), inherited.end());
    if (inherited.count(L"REGION_LAUNCHER_NETWORK"))
        for (auto name : {L"HTTP_PROXY", L"HTTPS_PROXY", L"ALL_PROXY", L"NO_PROXY"}) values.erase(name);
    std::vector<wchar_t> result;
    for (const auto& [name, value] : values) {
        result.insert(result.end(), name.begin(), name.end()); result.push_back(L'=');
        result.insert(result.end(), value.begin(), value.end()); result.push_back(0);
    }
    if (result.empty()) result.push_back(0);
    result.push_back(0);
    return result;
}
}
