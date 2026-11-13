# Releasing listeners after their creator exits

Run on Windows with the system Python:

```powershell
python experiments/port-recovery/shared_listener.py
```

The fixture creates TCP and UDP sockets on an ephemeral loopback port, shares
them with the test controller using `WSADuplicateSocket` through Python's
`socket.share`, then terminates and waits for the creator process. It checks
the Windows endpoint tables and attempts exclusive binds before and after
closing the surviving references. Separate TCP and UDP control sockets remain
usable throughout all three iterations. Only the fixture's child process is
terminated; no existing proxy ports or service settings are touched.

On the tested Windows host, both endpoint tables continued to report the dead
creator PID. TCP remained in LISTEN (state 2), UDP remained bound, and both
bind attempts failed with error 10048. Closing the shared TCP descriptor freed
the TCP port while UDP remained occupied. Closing the UDP descriptor freed
UDP as well. The holder process stayed alive. All three iterations passed.

This establishes a recovery mechanism for shared endpoint references, not a
diagnosis of the RegionLauncher outage. The fixture deliberately creates and
knows the remaining references. A production repair must identify the actual
references before asking their owner to close them. The original PID and port
alone do not identify every surviving reference. Closing an unrelated client
connection is not equivalent to closing the listening socket. Arbitrarily
closing handles in other processes is not an implemented recovery mechanism.

The launcher runtime is unchanged by this experiment. The live 17890 incident
had already been cleared by a reboot and was not reproduced here.

## External close acceptance check

```powershell
python experiments/port-recovery/forced_close.py
```

This second fixture creates its own child and uses the retained child process
handle to close exactly its known listening socket with
`DuplicateHandle(DUPLICATE_CLOSE_SOURCE)`. It accepts no external PID, handle,
or port. The original port becomes bindable, but the child still has the old
socket object. When the child allocates another socket with the recycled
handle value, normal cleanup of the old object closes the new socket. All
three local runs reproduced WSAENOTSOCK (10038) on the unrelated new socket.

This is a passing regression demonstration of an unsafe proposed recovery,
not a successful acceptance test for a launcher feature. Checking the
original handle identity immediately before the forced close cannot prevent
later reuse in the affected process. The runtime therefore does not include
this forced-close operation.

A separate one-shot probe of the private AFD_UNBIND control (0x12113) using
AF_INET and the respective TCP/UDP protocol on newly created idle loopback
sockets returned STATUS_INVALID_PARAMETER (TCP) and STATUS_NOT_SUPPORTED
(UDP). Both exclusive binds remained blocked with 10048. No existing proxy
or application handles were used. This did not establish a usable alternative.

References:

- [Microsoft: shared sockets](https://learn.microsoft.com/en-us/windows/win32/winsock/shared-sockets-2)
- [Microsoft: handle inheritance](https://learn.microsoft.com/en-us/windows/win32/sysinfo/handle-inheritance)
- [Microsoft: closesocket](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-closesocket)
- [Microsoft: DuplicateHandle socket restrictions](https://learn.microsoft.com/en-us/windows/win32/api/handleapi/nf-handleapi-duplicatehandle)
- [System Informer: native AFD definitions](https://github.com/winsiderss/systeminformer/blob/master/phnt/include/ntafd.h)
