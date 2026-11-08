// Offline experiment: real Windows process groups, upstream routing policy,
// and a minimal owner-record fixture. No packet filter or network calls.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <algorithm>
#include <cstdint>
#include <cwctype>
#include <iostream>
#include <map>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>

namespace iphelper {
// Only the data fields consumed by the upstream policy header are represented.
// This does not exercise upstream connection-table capture or PID attribution.
struct network_process {
    unsigned long id{};
    std::wstring name, path_name;
    bool resolved{true}, excluded{}, bypass_tcp{}, bypass_udp{};
    std::optional<uint16_t> tcp_proxy_port, udp_proxy_port;
    static std::wstring to_upper(std::wstring s) {
        std::transform(s.begin(), s.end(), s.begin(), ::towupper); return s;
    }
};
}
#include "process_routing_policy.h"

static void check(bool value, const char* message) {
    if (!value) throw std::runtime_error(message);
}
enum class Membership { inside, outside, unknown };
static Membership membership(HANDLE job, DWORD pid) {
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return Membership::unknown;
    BOOL inside = FALSE;
    BOOL ok = IsProcessInJob(process, job, &inside);
    CloseHandle(process);
    return !ok ? Membership::unknown : inside ? Membership::inside : Membership::outside;
}
struct Child {
    PROCESS_INFORMATION info{};
    Child(HANDLE job) {
        wchar_t path[32768]; check(GetModuleFileNameW(nullptr, path, 32768) != 0, "Executable path");
        std::wstring cmd = L"\"" + std::wstring(path) + L"\" --wait";
        STARTUPINFOW startup{sizeof(startup)};
        check(CreateProcessW(path, cmd.data(), nullptr, nullptr, FALSE,
              CREATE_SUSPENDED | CREATE_NO_WINDOW, nullptr, nullptr, &startup, &info), "Create child");
        if (job && !AssignProcessToJobObject(job, info.hProcess)) {
            TerminateProcess(info.hProcess, 1); CloseHandle(info.hThread); CloseHandle(info.hProcess);
            info = {}; throw std::runtime_error("Assign child to job");
        }
        // Keep children suspended: they never execute application code or access the network.
    }
    ~Child() {
        if (info.hProcess) { TerminateProcess(info.hProcess, 0); WaitForSingleObject(info.hProcess, 5000);
            CloseHandle(info.hProcess); CloseHandle(info.hThread); }
    }
};

int main(int argc, char**) {
    if (argc > 1) return 0;
    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    if (!job) return 1;
    try {
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        check(SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)), "Job limits");
        Child inside(job), outside(nullptr);
        check(membership(job, inside.info.dwProcessId) == Membership::inside, "Inside membership");
        check(membership(job, outside.info.dwProcessId) == Membership::outside, "Outside membership");
        check(membership(job, 0) == Membership::unknown, "Unknown PID must remain unknown");

        std::multimap<size_t, std::wstring> names{{0, L"SCOPEPROBE.EXE"}};
        std::vector<std::wstring> exclusions;
        proxy::owner_match_rules rules{names, exclusions, true, GetCurrentProcessId()};
        iphelper::network_process in{inside.info.dwProcessId, L"SCOPEPROBE.EXE", L"C:\\SCOPEPROBE.EXE"};
        iphelper::network_process out{outside.info.dwProcessId, L"SCOPEPROBE.EXE", L"C:\\SCOPEPROBE.EXE"};
        check(proxy::match_owner_to_app(L"SCOPEPROBE.EXE", in, rules), "Upstream inside name match");
        check(proxy::match_owner_to_app(L"SCOPEPROBE.EXE", out, rules), "Upstream also matches outside instance");

        const auto scoped_match = [&](auto& owner) {
            return membership(job, owner.id) == Membership::inside &&
                   proxy::match_owner_to_app(L"SCOPEPROBE.EXE", owner, rules);
        };
        check(scoped_match(in) && !scoped_match(out), "Job gate separates identical executable instances");

        // A scope check only inside the selection callback is insufficient: a cached
        // proxy port skips selection. The production policy header is invoked here.
        out.tcp_proxy_port = uint16_t{34010};
        bool selected = false;
        auto cached = proxy::route_owner<proxy::owner_transport::tcp, proxy::owner_family::ipv4>(out, [&] {
            selected = true; return proxy::proxy_port_result{};
        });
        check(!selected && cached.action == proxy::proxy_port_action::proxy, "Cached-port branch bypasses selection");
        std::cout << "PASS: real same-executable inside/outside processes distinguished by Windows job\n"
                  << "CONFIRMED: upstream name matching selects both instances\n"
                  << "CONFIRMED: cached proxy port skips selection; scope gate must precede cache\n"
                  << "LIMIT: owner records are fixtures; packet attribution and network isolation remain untested\n";
    } catch (const std::exception& e) {
        CloseHandle(job); std::cerr << e.what() << '\n'; return 1;
    }
    CloseHandle(job); return 0;
}
