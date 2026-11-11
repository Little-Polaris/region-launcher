#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <windns.h>
#include <iphlpapi.h>
#include <icmpapi.h>
#include <stdlib.h>
#include <string>
#include <atomic>
#include <new>
#include "detours.h"
#include "UnifiedDns.h"

static decltype(&DnsQuery_W) originalW = DnsQuery_W;
static decltype(&DnsQuery_A) originalA = DnsQuery_A;
static decltype(&DnsQuery_UTF8) originalUtf8 = DnsQuery_UTF8;
static decltype(&DnsQueryEx) originalEx = DnsQueryEx;
static DNS_CUSTOM_SERVER dnsServer = {};
static DNS_ADDR_ARRAY dnsAddresses = {};
static decltype(&Icmp6CreateFile) originalIcmp6 = Icmp6CreateFile;
static HANDLE WINAPI noIcmp6() { SetLastError(ERROR_NOT_SUPPORTED); return INVALID_HANDLE_VALUE; }
static void removeIPv6(PDNS_QUERY_RESULT result) {
    if (!result) return;
    PDNS_RECORD* link = &result->pQueryRecords;
    while (*link) {
        auto record = *link;
        if (record->wType == DNS_TYPE_AAAA) {
            *link = record->pNext; record->pNext = nullptr; DnsRecordListFree(record, DnsFreeRecordList);
        } else link = &record->pNext;
    }
    if (result->QueryStatus == ERROR_SUCCESS && !result->pQueryRecords) result->QueryStatus = DNS_INFO_NO_RECORDS;
}
static ULONG64 privateQueryOptions(ULONG64 options) {
    // Keep documented DNS semantics, not the system resolver's private routing flags.
    constexpr ULONG64 preserve = DNS_QUERY_ACCEPT_TRUNCATED_RESPONSE | DNS_QUERY_USE_TCP_ONLY |
        DNS_QUERY_NO_RECURSION | DNS_QUERY_DONT_RESET_TTL_VALUES | DNS_QUERY_DISABLE_IDN_ENCODING |
        DNS_QUERY_DNSSEC_OK | DNS_QUERY_DNSSEC_CHECKING_DISABLED | DNS_QUERY_DNSSEC_REQUIRED |
        DNS_QUERY_PARSE_ALL_RECORDS | DNS_QUERY_DUAL_ADDR | DNS_QUERY_ADDRCONFIG;
    return (options & preserve) | DNS_QUERY_BYPASS_CACHE | DNS_QUERY_WIRE_ONLY |
        DNS_QUERY_NO_NETBT | DNS_QUERY_NO_MULTICAST | DNS_QUERY_TREAT_AS_FQDN;
}
static decltype(&DnsQueryRaw) originalRaw = DnsQueryRaw;
static DNS_STATUS WINAPI queryRaw(DNS_QUERY_RAW_REQUEST* request, DNS_QUERY_RAW_CANCEL* cancel) {
    if (!request) return ERROR_INVALID_PARAMETER;
    if (dnsAddresses.AddrCount != 1) return ERROR_BAD_CONFIGURATION;
    if (request->version != 1) return ERROR_NOT_SUPPORTED;
    if (request->dnsQueryType == DNS_TYPE_AAAA) return DNS_INFO_NO_RECORDS;
    if (!request->dnsQueryName && request->dnsQueryRaw) {
        size_t offset = request->protocol == DNS_PROTOCOL_TCP ? 2 : 0;
        const auto bytes = request->dnsQueryRaw; size_t size = request->dnsQueryRawSize;
        if (size < offset + 12 || bytes[offset+4] != 0 || bytes[offset+5] != 1) return DNS_ERROR_BAD_PACKET;
        offset += 12;
        while (offset < size && bytes[offset]) {
            if (bytes[offset] > 63 || offset + bytes[offset] + 1 >= size) return DNS_ERROR_BAD_PACKET;
            offset += bytes[offset] + 1;
        }
        if (++offset + 4 > size) return DNS_ERROR_BAD_PACKET;
        if (bytes[offset] == 0 && bytes[offset+1] == DNS_TYPE_AAAA) return DNS_INFO_NO_RECORDS;
    }
    if (request->queryOptions & DNS_QUERY_CACHE_ONLY) return DNS_INFO_NO_RECORDS;
    DNS_QUERY_RAW_REQUEST copy = *request;
    copy.customServersSize = 1; copy.customServers = &dnsServer; copy.interfaceIndex = 0;
    copy.queryOptions = privateQueryOptions(request->queryOptions);
    return originalRaw(&copy, cancel);
}
struct QueryContext {
    DNS_QUERY_REQUEST3 request = {};
    PDNS_QUERY_COMPLETION_ROUTINE callback = nullptr;
    void* context = nullptr;
    std::atomic<int> refs{2};
    std::atomic<bool> completed{false};
    void release() { if (--refs == 0) delete this; }
};
static void WINAPI completed(void* opaque, PDNS_QUERY_RESULT result) {
    auto state = static_cast<QueryContext*>(opaque);
    if (!state->completed.exchange(true)) {
        removeIPv6(result);
        state->callback(state->context, result);
        state->release();
    }
}
static bool isLocalhost(PCWSTR name) {
    if (!name) return false;
    size_t length = wcslen(name);
    if (length && name[length - 1] == L'.') --length;
    constexpr size_t suffix = 9; // localhost, including its RFC 6761 subdomains.
    return length >= suffix && (length == suffix || name[length - suffix - 1] == L'.') &&
        _wcsnicmp(name + length - suffix, L"localhost", suffix) == 0;
}
static DNS_STATUS localQuery(PDNS_QUERY_REQUEST request, PDNS_QUERY_RESULT result) {
    result->pQueryRecords = nullptr;
    result->QueryOptions = request->QueryOptions;
    result->QueryStatus = DNS_INFO_NO_RECORDS;
    if (request->QueryType == DNS_TYPE_A || request->QueryType == DNS_TYPE_ANY ||
        (request->QueryType == DNS_TYPE_AAAA && (request->QueryOptions & DNS_QUERY_DUAL_ADDR))) {
        DNS_RECORDW record = {};
        record.pName = const_cast<PWSTR>(request->QueryName);
        record.wType = DNS_TYPE_A; record.wDataLength = sizeof(DNS_A_DATA);
        record.Flags.S.Section = DnsSectionAnswer;
        record.Flags.S.CharSet = DnsCharSetUnicode;
        record.Data.A.IpAddress = htonl(INADDR_LOOPBACK);
        result->pQueryRecords = DnsRecordSetCopyEx(reinterpret_cast<PDNS_RECORD>(&record), DnsCharSetUnicode, DnsCharSetUnicode);
        result->QueryStatus = result->pQueryRecords ? ERROR_SUCCESS : ERROR_NOT_ENOUGH_MEMORY;
    }
    // Immediate DNS_QUERY completion does not invoke the asynchronous callback.
    // Resolve locally even with CACHE_ONLY/WIRE_ONLY; never consult host DNS or hosts.
    return result->QueryStatus;
}
static DNS_STATUS WINAPI queryEx(PDNS_QUERY_REQUEST request, PDNS_QUERY_RESULT result, PDNS_QUERY_CANCEL cancel) {
    if (!request || !result) return ERROR_INVALID_PARAMETER;
    if (dnsAddresses.AddrCount != 1) return ERROR_BAD_CONFIGURATION;
    if (request->Version != 1 && request->Version != 3) return ERROR_NOT_SUPPORTED;
    if (isLocalhost(request->QueryName)) return localQuery(request, result);
    // The private resolver owns its cache. Never read or populate the host cache.
    bool dual = (request->QueryOptions & DNS_QUERY_DUAL_ADDR) != 0;
    if ((request->QueryType == DNS_TYPE_AAAA && !dual) || (request->QueryOptions & DNS_QUERY_CACHE_ONLY)) {
        result->QueryStatus = DNS_INFO_NO_RECORDS; result->pQueryRecords = nullptr;
        return DNS_INFO_NO_RECORDS;
    }
    auto state = new (std::nothrow) QueryContext;
    if (!state) return ERROR_NOT_ENOUGH_MEMORY;
    memcpy(&state->request, request, request->Version == 3 ? sizeof(DNS_QUERY_REQUEST3) : sizeof(DNS_QUERY_REQUEST));
    auto& query = state->request;
    query.Version = 1;
    query.pDnsServerList = &dnsAddresses; query.InterfaceIndex = 0;
    query.cCustomServers = 0; query.pCustomServers = nullptr;
    query.IsNetworkQueryRequired = FALSE; query.RequiredNetworkIndex = 0;
    query.QueryOptions = privateQueryOptions(request->QueryOptions);
    if (dual) { query.QueryType = DNS_TYPE_A; query.QueryOptions &= ~((ULONG64)DNS_QUERY_DUAL_ADDR); }
    state->callback = query.pQueryCompletionCallback; state->context = query.pQueryContext;
    if (state->callback) { query.pQueryCompletionCallback = completed; query.pQueryContext = state; }
    DWORD status = originalEx(reinterpret_cast<PDNS_QUERY_REQUEST>(&query), result, cancel);
    if (status != DNS_REQUEST_PENDING) {
        removeIPv6(result);
        if (status == ERROR_SUCCESS) status = result->QueryStatus;
    }
    if (status != DNS_REQUEST_PENDING && !state->completed.exchange(true)) state->release();
    state->release();
    return status;
}

static DNS_STATUS WINAPI queryW(PCWSTR name, WORD type, DWORD options, PVOID extra, PDNS_RECORD* records, PVOID* reserved) {
    if (!name || !records || reserved) return ERROR_INVALID_PARAMETER;
    *records = nullptr;
    DNS_QUERY_REQUEST query = {}; query.Version = DNS_QUERY_REQUEST_VERSION1;
    query.QueryName = name; query.QueryType = type; query.QueryOptions = options;
    DNS_QUERY_RESULT result = {}; result.Version = DNS_QUERY_RESULTS_VERSION1;
    DWORD status = DnsQueryEx(&query, &result, nullptr);
    *records = result.pQueryRecords;
    return status;
}
static DNS_STATUS queryText(PCSTR name, WORD type, DWORD options, PDNS_RECORD* records, PVOID* reserved, UINT cp, DNS_CHARSET charset) {
    if (!name || !records || reserved) return ERROR_INVALID_PARAMETER;
    int size = MultiByteToWideChar(cp, 0, name, -1, nullptr, 0);
    if (!size) return ERROR_INVALID_NAME;
    std::wstring wide(size, 0); MultiByteToWideChar(cp, 0, name, -1, wide.data(), size);
    PDNS_RECORD source = nullptr; *records = nullptr;
    DWORD status = queryW(wide.c_str(), type, options, nullptr, &source, nullptr);
    if (source) {
        *records = DnsRecordSetCopyEx(source, DnsCharSetUnicode, charset);
        DnsRecordListFree(source, DnsFreeRecordList);
        if (!*records) return ERROR_NOT_ENOUGH_MEMORY;
    }
    return status;
}
static DNS_STATUS WINAPI queryA(PCSTR name, WORD type, DWORD options, PVOID, PDNS_RECORD* records, PVOID* reserved) {
    return queryText(name, type, options, records, reserved, CP_ACP, DnsCharSetAnsi);
}
static DNS_STATUS WINAPI queryUtf8(PCSTR name, WORD type, DWORD options, PVOID, PDNS_RECORD* records, PVOID* reserved) {
    return queryText(name, type, options, records, reserved, CP_UTF8, DnsCharSetUtf8);
}
bool AttachIPv4Hooks() {
    // Winsock's IPv4 resolver itself uses an IPv6 handle for address sorting.
    // TCP/UDP IPv6 is blocked by the process-scoped packet engine, not socket creation.
    return DetourAttach(&(PVOID&)originalIcmp6, noIcmp6) == NO_ERROR;
}
bool AttachUnifiedDnsHooks() {
    return DetourAttach(&(PVOID&)originalEx, queryEx) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalRaw, queryRaw) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalW, queryW) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalA, queryA) == NO_ERROR &&
        DetourAttach(&(PVOID&)originalUtf8, queryUtf8) == NO_ERROR;
}

bool ConfigureUnifiedDns() {
    WCHAR address[64] = {};
    if (!GetEnvironmentVariableW(L"REGION_LAUNCHER_DNS_ADDRESS", address, _countof(address))) return true;
    DNS_CUSTOM_SERVER& server = dnsServer;
    server.dwServerType = DNS_CUSTOM_SERVER_TYPE_UDP;
    server.ServerAddr.Ipv4.sin_family = AF_INET;
    if (InetPtonW(AF_INET, address, &server.ServerAddr.Ipv4.sin_addr) != 1 ||
        (ntohl(server.ServerAddr.Ipv4.sin_addr.S_un.S_addr) >> 24) != 127) {
        SetLastError(ERROR_INVALID_PARAMETER); return false;
    }
    // The API server address uses port zero for standard DNS port 53.
    dnsAddresses.MaxCount = dnsAddresses.AddrCount = 1;
    memcpy(dnsAddresses.AddrArray[0].MaxSa, &server.ServerAddr.Ipv4, sizeof(sockaddr_in));
    return true;
}
