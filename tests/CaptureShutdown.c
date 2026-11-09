// Exercise the real packet loop and Stop with a deterministic WinDivert queue.
// The WinDivert helper parser is real; no driver is opened or traffic captured.
#include "../vendor/proxybridge/pb_internal.h"

static HANDLE recv_entered, shutdown_recv;
static LONG received, sent, failed_sends, closed;
static BOOL send_disabled;
static const UINT packet_count = 3;
static unsigned char expected[40];

static BOOL test_recv(HANDLE handle, PVOID packet, UINT size, UINT *length, WINDIVERT_ADDRESS *addr)
{
    SetEvent(recv_entered);
    if (WaitForSingleObject(shutdown_recv, 5000) != WAIT_OBJECT_0) ExitProcess(20);
    if (received >= (LONG)packet_count) { SetLastError(ERROR_NO_DATA); return FALSE; }
    if (size < sizeof(expected)) ExitProcess(21);
    memcpy(packet, expected, sizeof(expected));
    *length = sizeof(expected);
    memset(addr, 0, sizeof(*addr)); addr->Outbound = 1;
    addr->IPChecksum = addr->TCPChecksum = 1;
    InterlockedIncrement(&received);
    return TRUE;
}
static BOOL test_send(HANDLE handle, const VOID *packet, UINT length, UINT *written, const WINDIVERT_ADDRESS *addr)
{
    if (closed || send_disabled || handle == INVALID_HANDLE_VALUE) {
        InterlockedIncrement(&failed_sends); SetLastError(ERROR_INVALID_HANDLE); return FALSE;
    }
    if (length != sizeof(expected) || memcmp(packet, expected, sizeof(expected))) ExitProcess(22);
    InterlockedIncrement(&sent); return TRUE;
}
static BOOL test_shutdown(HANDLE handle, WINDIVERT_SHUTDOWN how)
{
    if (how & WINDIVERT_SHUTDOWN_SEND) send_disabled = TRUE;
    if (how & WINDIVERT_SHUTDOWN_RECV) SetEvent(shutdown_recv);
    return TRUE;
}
static BOOL test_close(HANDLE handle) { InterlockedExchange(&closed, 1); return TRUE; }

#define WinDivertRecv test_recv
#define WinDivertSend test_send
#define WinDivertShutdown test_shutdown
#define WinDivertClose test_close
#pragma warning(push)
#pragma warning(disable:4005) // The production send wrapper wraps the mocked send.
#include "../vendor/proxybridge/ProxyBridge.c"
#pragma warning(pop)

int main(int argc, char **argv)
{
    WSADATA wsa; WSAStartup(MAKEWORD(2,2), &wsa);
    InitializeSRWLock(&lock);
    WINDIVERT_IPHDR *ip = (WINDIVERT_IPHDR *)expected;
    WINDIVERT_TCPHDR *tcp = (WINDIVERT_TCPHDR *)(expected + 20);
    ip->Version=4; ip->HdrLength=5; ip->Length=htons(sizeof(expected));
    ip->TTL=64; ip->Protocol=IPPROTO_TCP;
    ip->SrcAddr=htonl(0xc0000201); ip->DstAddr=htonl(0xc6336401);
    tcp->SrcPort=htons(40000); tcp->DstPort=htons(443); tcp->HdrLength=5; tcp->Ack=1;
    // An established outside TCP flow has already received a DIRECT verdict.
    port_set_direct(40000);
    recv_entered=CreateEventW(NULL,TRUE,FALSE,NULL);
    shutdown_recv=CreateEventW(NULL,TRUE,FALSE,NULL);
    running=TRUE; windivert_handle=(HANDLE)(uintptr_t)123;
    if (argc == 1) {
        packet_thread[0]=CreateThread(NULL,0,packet_processor,NULL,0,NULL);
        if (WaitForSingleObject(recv_entered,5000)!=WAIT_OBJECT_0) return 23;
    }
    if (!ProxyBridge_Stop()) return 24;
    printf("queued=%u received=%ld sent=%ld failed_sends=%ld closed=%ld\n",
           packet_count,received,sent,failed_sends,closed);
    CloseHandle(recv_entered); CloseHandle(shutdown_recv); WSACleanup();
    if (sent != (LONG)packet_count || failed_sends || !closed) return 1;
    puts("PASS: outside queued packets forwarded unchanged before capture handle closes");
    return 0;
}
