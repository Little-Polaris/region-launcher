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

References:

- [Microsoft: shared sockets](https://learn.microsoft.com/en-us/windows/win32/winsock/shared-sockets-2)
- [Microsoft: handle inheritance](https://learn.microsoft.com/en-us/windows/win32/sysinfo/handle-inheritance)
- [Microsoft: closesocket](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-closesocket)
