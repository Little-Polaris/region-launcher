#include "../src/ChildEnvironment.h"
#include <cassert>
#include <iostream>

int main() {
    using namespace ChildEnvironment;
    Values saved{{L"REGION_LAUNCHER_LOCALE", L"en-US"}, {L"REGION_LAUNCHER_NETWORK", L"1"}, {L"TZ", L"America/Los_Angeles"}};
    const char ansi[] = "CUSTOM=kept\0http_proxy=http://127.0.0.1:7890\0region_launcher_locale=ja-JP\0=C:=C:\\work\0";
    auto block = build((void*)ansi, 0, saved);
    Values actual;
    for (auto p = block.data(); *p; p += wcslen(p) + 1) add(actual, p);
    assert(actual.at(L"CUSTOM") == L"kept" && actual.at(L"=C:") == L"C:\\work");
    assert(actual.at(L"REGION_LAUNCHER_LOCALE") == L"en-US" && actual.at(L"TZ") == L"America/Los_Angeles");
    assert(!actual.count(L"HTTP_PROXY") && actual.size() == 5);
    const wchar_t wide[] = L"CUSTOM=\u4e2d\u6587 \U0001F680\0HTTPS_PROXY=http://local\0";
    auto unicode = build((void*)wide, CREATE_UNICODE_ENVIRONMENT, saved);
    actual.clear();
    for (auto p = unicode.data(); *p; p += wcslen(p) + 1) add(actual, p);
    assert(actual.at(L"CUSTOM") == L"\u4e2d\u6587 \U0001F680" && !actual.count(L"HTTPS_PROXY"));
    wchar_t empty[2] = {};
    auto minimal = build(empty, CREATE_UNICODE_ENVIRONMENT, {});
    assert(minimal.size() == 2 && !minimal[0] && !minimal[1]);
    std::cout << "PASS: ANSI/Unicode child environment, case-insensitive ownership, proxy removal, drive entries, empty block\n";
}
