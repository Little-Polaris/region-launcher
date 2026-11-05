#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <windns.h>
#include <iphlpapi.h>
#include <icmpapi.h>
#include <stdio.h>
#include <string>
#include <vector>

static void WINAPI dnsDone(void* event, PDNS_QUERY_RESULT) { SetEvent((HANDLE)event); }
struct RawResult { HANDLE event; DNS_QUERY_RAW_RESULT* result = nullptr; };
static void WINAPI rawDone(void* context, DNS_QUERY_RAW_RESULT* result) {
    auto state = (RawResult*)context; state->result = result; SetEvent(state->event);
}
static bool readAll(SOCKET s, char* data, int size) {
    while (size) { int n = recv(s, data, size, 0); if (n <= 0) return false; data += n; size -= n; }
    return true;
}
static void rawDns(const wchar_t* name, bool tcp, bool ipv6 = false) {
    std::vector<unsigned char> packet{0x12,0x34,1,0,0,1,0,0,0,0,0,0};
    std::wstring domain(name); size_t start = 0;
    while (start < domain.size()) {
        size_t end = domain.find(L'.', start); if (end == std::wstring::npos) end = domain.size();
        packet.push_back((unsigned char)(end-start));
        for (size_t i = start; i < end; ++i) packet.push_back((unsigned char)domain[i]);
        start = end + 1;
    }
    for (auto byte : {0,0,1,0,1}) packet.push_back(byte);
    SOCKET s = socket(ipv6 ? AF_INET6 : AF_INET, tcp ? SOCK_STREAM : SOCK_DGRAM, 0);
    if (s == INVALID_SOCKET) { printf("RawDNS-%s-v%d status=%d\n", tcp ? "TCP" : "UDP", ipv6 ? 6 : 4, WSAGetLastError()); return; }
    DWORD timeout = 3500; setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));
    sockaddr_in target = {}; target.sin_family = AF_INET; target.sin_port = htons(53); InetPtonW(AF_INET, L"127.0.0.2", &target.sin_addr);
    sockaddr_in6 target6 = {}; target6.sin6_family = AF_INET6; target6.sin6_port = htons(53); InetPtonW(AF_INET6, L"::1", &target6.sin6_addr);
    WCHAR testTarget[128] = {};
    if (ipv6 && GetEnvironmentVariableW(L"DNS_PROBE_IPV6_TARGET", testTarget, _countof(testTarget))) {
        WCHAR* scope = wcschr(testTarget, L'%');
        if (scope) { *scope++ = 0; target6.sin6_scope_id = wcstoul(scope, nullptr, 10); }
        if (InetPtonW(AF_INET6, testTarget, &target6.sin6_addr) != 1) { closesocket(s); puts("Invalid IPv6 fixture address"); return; }
    }
    u_long mode = 1; if (tcp) ioctlsocket(s, FIONBIO, &mode);
    bool ok = connect(s, ipv6 ? (sockaddr*)&target6 : (sockaddr*)&target, ipv6 ? sizeof(target6) : sizeof(target)) == 0;
    if (!ok && tcp && WSAGetLastError() == WSAEWOULDBLOCK) {
        fd_set write, error; FD_ZERO(&write); FD_ZERO(&error); FD_SET(s,&write); FD_SET(s,&error);
        timeval wait = {3,0}; int errorCode = 0, length = sizeof(errorCode);
        ok = select(0,nullptr,&write,&error,&wait) > 0 && FD_ISSET(s,&write);
        if (ok) { getsockopt(s,SOL_SOCKET,SO_ERROR,(char*)&errorCode,&length); ok = errorCode == 0; }
        if (!ok) WSASetLastError(WSAETIMEDOUT);
    }
    mode = 0; if (tcp) ioctlsocket(s,FIONBIO,&mode);
    if (ok && tcp) { unsigned short size = htons((unsigned short)packet.size()); ok = send(s, (char*)&size, 2, 0) == 2; }
    if (ok) ok = send(s, (char*)packet.data(), (int)packet.size(), 0) == packet.size();
    char reply[4096]; int n = 0;
    if (ok && tcp) { unsigned short size; ok = readAll(s, (char*)&size, 2); if (ok) { n = ntohs(size); ok = n <= sizeof(reply) && readAll(s, reply, n); } }
    else if (ok) { n = recv(s, reply, sizeof(reply), 0); ok = n >= 12; }
    int result = ok ? 0 : WSAGetLastError();
    if (!ok && !result) result = WSAECONNABORTED; // Later socket calls may clear the failed operation's last error.
    printf("RawDNS-%s-v%d status=%d", tcp ? "TCP" : "UDP", ipv6 ? 6 : 4, result);
    if (ok && n >= 16) { char ip[64]; InetNtopA(AF_INET, reply+n-4, ip, sizeof(ip)); printf(" ip=%s", ip); }
    puts(""); closesocket(s);
}

static void records(const char* api, DWORD status, PDNS_RECORD result) {
    printf("%s status=%lu", api, status);
    for (auto r = result; r; r = r->pNext) if (r->wType == DNS_TYPE_A) {
        char ip[64]; InetNtopA(AF_INET, &r->Data.A.IpAddress, ip, sizeof(ip)); printf(" ip=%s", ip);
    } else if (r->wType == DNS_TYPE_AAAA) {
        char ip[64]; InetNtopA(AF_INET6, &r->Data.AAAA.Ip6Address, ip, sizeof(ip)); printf(" ip=%s", ip);
    }
    puts("");
    if (result) DnsRecordListFree(result, DnsFreeRecordList);
}
int wmain(int argc, wchar_t** argv) {
    if (argc < 2) return 2;
    WSADATA ws; WSAStartup(MAKEWORD(2,2), &ws);
    if (argc > 2 && !wcscmp(argv[2], L"raw-only")) {
        rawDns(argv[1], false); rawDns(argv[1], true); rawDns(argv[1], false, true); rawDns(argv[1], true, true);
        fflush(stdout); Sleep(1500); return 0;
    }
    SOCKET sixW = WSASocketW(AF_INET6,SOCK_DGRAM,0,nullptr,0,0), sixA = WSASocketA(AF_INET6,SOCK_STREAM,0,nullptr,0,0);
    HANDLE icmp = Icmp6CreateFile();
    printf("IPv6 WSASocketW=%s WSASocketA=%s Icmp6=%s\n",sixW == INVALID_SOCKET ? "blocked" : "available",sixA == INVALID_SOCKET ? "blocked" : "available",icmp == INVALID_HANDLE_VALUE ? "blocked" : "available");
    if (sixW != INVALID_SOCKET) closesocket(sixW); if (sixA != INVALID_SOCKET) closesocket(sixA); if (icmp != INVALID_HANDLE_VALUE) IcmpCloseHandle(icmp);
    DWORD count = 0; DNS_CUSTOM_SERVER* servers = nullptr;
    DNS_APPLICATION_SETTINGS settings = {DNS_APP_SETTINGS_VERSION1};
    DWORD error = DnsGetApplicationSettings(&count, &servers, &settings);
    printf("settings status=%lu servers=%lu exclusive=%llu\n", error, count, settings.Flags);
    if (servers) DnsFreeCustomServers(&count, &servers);
    PDNS_RECORD rec = nullptr;
    DWORD status = DnsQuery_W(argv[1], DNS_TYPE_A, DNS_QUERY_STANDARD, nullptr, &rec, nullptr);
    records("DnsQueryW", status, rec);
    rec = nullptr; status = DnsQuery_W(argv[1], DNS_TYPE_AAAA, 0, nullptr, &rec, nullptr); records("DnsQueryAAAA", status, rec);
    char narrow[512]; WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, narrow, sizeof(narrow), nullptr, nullptr);
    rec = nullptr; status = DnsQuery_A(narrow, DNS_TYPE_A, 0, nullptr, &rec, nullptr); records("DnsQueryA", status, rec);
    rec = nullptr; status = DnsQuery_UTF8(narrow, DNS_TYPE_A, 0, nullptr, &rec, nullptr); records("DnsQueryUTF8", status, rec);
    ADDRINFOW hint = {}; hint.ai_family = AF_INET; hint.ai_socktype = SOCK_STREAM;
    ADDRINFOW* addresses = nullptr;
    status = GetAddrInfoW(argv[1], L"443", &hint, &addresses);
    printf("GetAddrInfoW status=%lu", status);
    for (auto a = addresses; a; a = a->ai_next) {
        char ip[64]; InetNtopA(AF_INET, &((sockaddr_in*)a->ai_addr)->sin_addr, ip, sizeof(ip)); printf(" ip=%s", ip);
    }
    puts(""); if (addresses) FreeAddrInfoW(addresses);
    hint.ai_family = AF_UNSPEC; addresses = nullptr;
    status = GetAddrInfoW(argv[1], L"443", &hint, &addresses);
    printf("GetAddrInfoUnspec status=%lu", status);
    for (auto a = addresses; a; a = a->ai_next) if (a->ai_family == AF_INET) {
        char ip[64]; InetNtopA(AF_INET, &((sockaddr_in*)a->ai_addr)->sin_addr, ip, sizeof(ip)); printf(" ip=%s", ip); break;
    }
    puts(""); if (addresses) FreeAddrInfoW(addresses);
    ADDRINFOA hintsA = {}; hintsA.ai_family = AF_INET; hintsA.ai_socktype = SOCK_STREAM; ADDRINFOA* a = nullptr;
    status = getaddrinfo(narrow, "443", &hintsA, &a);
    printf("getaddrinfo status=%lu", status);
    if (a) { char ip[64]; InetNtopA(AF_INET, &((sockaddr_in*)a->ai_addr)->sin_addr, ip, sizeof(ip)); printf(" ip=%s", ip); freeaddrinfo(a); }
    puts("");
    hostent* h = gethostbyname(narrow); printf("gethostbyname status=%d", h ? 0 : WSAGetLastError());
    if (h && h->h_addr_list[0]) { char ip[64]; InetNtopA(AF_INET, h->h_addr_list[0], ip, sizeof(ip)); printf(" ip=%s", ip); } puts("");
    DNS_QUERY_REQUEST query = {}; query.Version = DNS_QUERY_REQUEST_VERSION1; query.QueryName = argv[1]; query.QueryType = DNS_TYPE_A;
    DNS_QUERY_RESULT result = {}; result.Version = DNS_QUERY_RESULTS_VERSION1;
    status = DnsQueryEx(&query, &result, nullptr);
    records("DnsQueryEx", status, result.pQueryRecords);
    HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    query.pQueryCompletionCallback = dnsDone; query.pQueryContext = event;
    result = {}; result.Version = DNS_QUERY_RESULTS_VERSION1;
    DNS_QUERY_CANCEL cancel = {}; status = DnsQueryEx(&query, &result, &cancel);
    printf("DnsQueryEx-async pending=%d\n", status == DNS_REQUEST_PENDING);
    if (status == DNS_REQUEST_PENDING) {
        if (WaitForSingleObject(event, 15000) != WAIT_OBJECT_0) { DnsCancelQuery(&cancel); return 4; }
        status = result.QueryStatus;
    }
    records("DnsQueryEx-async", status, result.pQueryRecords); CloseHandle(event);
    RawResult rawState = {CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    DNS_QUERY_RAW_REQUEST rawQuery = {}; rawQuery.version = 1; rawQuery.resultsVersion = 1;
    rawQuery.dnsQueryName = argv[1]; rawQuery.dnsQueryType = DNS_TYPE_A; rawQuery.protocol = DNS_PROTOCOL_UDP;
    rawQuery.queryCompletionCallback = rawDone; rawQuery.queryContext = &rawState;
    rawQuery.sourceAddr.Ipv4.sin_family = AF_INET; rawQuery.sourceAddr.Ipv4.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    DNS_QUERY_RAW_CANCEL rawCancel = {}; status = DnsQueryRaw(&rawQuery, &rawCancel);
    if (status == DNS_REQUEST_PENDING) {
        if (WaitForSingleObject(rawState.event, 15000) != WAIT_OBJECT_0) { DnsCancelQueryRaw(&rawCancel); return 5; }
        status = rawState.result->queryStatus;
    }
    PDNS_RECORD copy = rawState.result ? DnsRecordSetCopyEx(rawState.result->queryRecords, DnsCharSetUnicode, DnsCharSetUnicode) : nullptr;
    records("DnsQueryRaw", status, copy);
    if (rawState.result) DnsQueryRawResultFree(rawState.result); CloseHandle(rawState.event);
    bool raw = argc > 3 && !wcscmp(argv[3], L"raw");
    if (raw) { rawDns(argv[1], false); rawDns(argv[1], true); rawDns(argv[1], false, true); rawDns(argv[1], true, true); }
    if (argc > 2 && !wcscmp(argv[2], L"child")) {
        WCHAR self[MAX_PATH]; GetModuleFileNameW(nullptr, self, _countof(self));
        std::wstring command = L"\"" + std::wstring(self) + L"\" " + argv[1] + (raw ? L" leaf raw" : L"");
        STARTUPINFOW si = {sizeof(si)}; PROCESS_INFORMATION pi = {};
        if (!CreateProcessW(nullptr, command.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &si, &pi)) return 3;
        WaitForSingleObject(pi.hProcess, 20000); CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    }
    fflush(stdout); Sleep(1500); WSACleanup(); return 0;
}
