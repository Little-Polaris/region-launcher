using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

static class ListenerRecoveryTests
{
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    public static async Task Run(string folder, string fixture)
    {
        Directory.CreateDirectory(folder);
        List<string> logs = [];
        void Log(string s) { lock (logs) logs.Add(s); Console.WriteLine(s); }
        using var outside = new TcpListener(IPAddress.Loopback, 0); outside.Start();
        int outsidePort = ((IPEndPoint)outside.LocalEndpoint).Port;
        async Task OutsideWorks() {
            using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, outsidePort);
            using var accepted = await outside.AcceptTcpClientAsync();
            await accepted.GetStream().WriteAsync(new byte[] { 42 });
            byte[] b = new byte[1]; Check(await c.GetStream().ReadAsync(b) == 1 && b[0] == 42, "Outside socket failed");
        }
        for (int pass = 0; pass < 3; pass++) {
            var psi = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(fixture); psi.ArgumentList.Add("--creator");
            using var creator = Process.Start(psi)!;
            using var oldIdentity = Process.GetProcessById(creator.Id);
            _ = oldIdentity.SafeHandle;
            List<Process> children = [];
            DedicatedProxy? proxy = null;
            try {
                _ = creator.SafeHandle;
                string json = await creator.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) ?? throw new IOException(await creator.StandardError.ReadToEndAsync());
                using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
                int port = root.GetProperty("port").GetInt32(), dnsPort = root.GetProperty("dnsPort").GetInt32();
                var holders = root.GetProperty("holders").EnumerateArray().Select(p => Process.GetProcessById(p.GetInt32())).ToArray();
                children.AddRange(holders);
                var client = Process.GetProcessById(root.GetProperty("client").GetInt32()); children.Add(client);
                foreach (var child in children) _ = child.SafeHandle;
                var ports = new ProxyPorts(port, IPAddress.Parse("127.0.0.154"), dnsPort);
                await OutsideWorks();
                proxy = new DedicatedProxy(Log);
                var fields = BindingFlags.NonPublic | BindingFlags.Instance;
                typeof(DedicatedProxy).GetField("process", fields)!.SetValue(proxy, creator);
                typeof(DedicatedProxy).GetField("ports", fields)!.SetValue(proxy, ports);
                if (pass != 0) { creator.Kill(); await creator.WaitForExitAsync(); }
                if (pass == 2) {
                    bool initiallyReleased = await Task.Run(() => (bool)typeof(DedicatedProxy).GetMethod("StopCore", fields)!.Invoke(proxy, [false])!);
                    Check(!initiallyReleased && !client.HasExited && holders.All(p => !p.HasExited), "Normal cleanup unexpectedly terminated holders");
                    await PublishedWorker(oldIdentity, ports, Log);
                }
                bool released = await Task.Run(() => (bool)typeof(DedicatedProxy).GetMethod("StopCore", fields)!.Invoke(proxy, [true])!);
                Check(released && proxy.CleanupComplete, "Shared TCP/UDP listeners were not released");
                foreach (var holder in holders) Check(holder.HasExited, "Actual shared listener holder survived");
                Check(!client.HasExited, "Ordinary TCP/UDP client was killed");
                ports.RequireAvailable(Log); await OutsideWorks();
                await RestartAndTransfer(folder, port, dnsPort, ports, Log);
                await OutsideWorks();
                using var replacement = new TcpListener(IPAddress.Loopback, port); replacement.Start();
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.ExclusiveAddressUse = true; udp.Bind(new IPEndPoint(IPAddress.Loopback, port));
                // Reusing the old process identity cannot authorize terminating a new owner of the same port.
                await ListenerRecovery.RecoverAsync(oldIdentity, ports, Log);
                Check(replacement.Server.IsBound && udp.IsBound && !client.HasExited, "New owner or client was affected");
                using var reconnect = new TcpClient(); await reconnect.ConnectAsync(IPAddress.Loopback, port);
                using var acceptedAgain = await replacement.AcceptTcpClientAsync();
                Log($"PASS {pass + 1}: shared TCP+UDP+DNS holders terminated, client survives, original ports rebound by real Mihomo, TCP/UDP/DNS transfer succeeds, new owner preserved");
            } finally {
                proxy?.Dispose();
                foreach (var child in children) { if (!child.HasExited) child.Kill(); child.Dispose(); }
                try { if (!creator.HasExited) creator.Kill(); } catch (InvalidOperationException) { }
                lock (logs) File.WriteAllLines(Path.Combine(folder, "listener-recovery.log"), logs);
            }
        }
    }

    static async Task PublishedWorker(Process old, ProxyPorts ports, Action<string> log)
    {
        string exe = Environment.GetEnvironmentVariable("REGION_TEST_PUBLISHED") ?? throw new Exception("REGION_TEST_PUBLISHED required");
        using var parent = Process.GetCurrentProcess();
        var request = new { LauncherPid = parent.Id, LauncherCreated = parent.StartTime.ToUniversalTime().ToFileTimeUtc(),
            OldHandle = old.SafeHandle.DangerousGetHandle().ToInt64(), OldPid = old.Id, OldCreated = old.StartTime.ToUniversalTime().ToFileTimeUtc(),
            Port = ports.Endpoints[0].Port, Dns = ports.Endpoints[2].Address.ToString(), DnsPort = ports.Endpoints[2].Port };
        var psi = new ProcessStartInfo(exe, "--recover-listeners") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using var worker = Process.Start(psi)!;
        var output = Task.WhenAll(worker.StandardOutput.ReadToEndAsync(), worker.StandardError.ReadToEndAsync());
        try {
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request)); worker.StandardInput.Close();
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            foreach (string text in await output) log(text);
            Check(worker.ExitCode == 0, "Published WinExe helper failed");
        } finally { if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(); } }
    }

    static async Task RestartAndTransfer(string folder, int port, int dnsPort, ProxyPorts ports, Action<string> log)
    {
        string engine = Environment.GetEnvironmentVariable("REGION_TEST_ENGINE") ?? throw new Exception("REGION_TEST_ENGINE required");
        string runtime = Path.GetFullPath(Path.Combine(folder, "recovered-core.json"));
        var config = new JsonObject {
            ["mixed-port"] = port, ["bind-address"] = "127.0.0.1", ["ipv6"] = false, ["log-level"] = "warning",
            ["hosts"] = new JsonObject { ["recovery.test"] = "192.0.2.123" },
            ["dns"] = new JsonObject { ["enable"] = true, ["listen"] = $"127.0.0.154:{dnsPort}", ["ipv6"] = false,
                ["use-hosts"] = true, ["nameserver"] = new JsonArray("1.1.1.1") },
            ["rules"] = new JsonArray("MATCH,DIRECT")
        };
        await File.WriteAllTextAsync(runtime, config.ToJsonString());
        var psi = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-d", Path.GetFullPath(folder), "-f", runtime }) psi.ArgumentList.Add(arg);
        using var core = Process.Start(psi)!;
        var output = Task.WhenAll(core.StandardOutput.ReadToEndAsync(), core.StandardError.ReadToEndAsync());
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = timeout.Token;
            for (;;) {
                Check(!core.HasExited, "Restarted Mihomo exited");
                try { using var ready = new TcpClient(); await ready.ConnectAsync(IPAddress.Loopback, port, ct); break; }
                catch (SocketException) { await Task.Delay(100, ct); }
            }
            using var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
            int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            var serve = Task.Run(async () => {
                using var accepted = await target.AcceptTcpClientAsync(ct);
                using var reader = new StreamReader(accepted.GetStream(), Encoding.ASCII, false, 1024, true);
                while (await reader.ReadLineAsync(ct) is string line && line.Length != 0) { }
                await accepted.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 9\r\nConnection: close\r\n\r\nrecovered"), ct);
            }, ct);
            using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{port}"), UseProxy = true };
            using var http = new HttpClient(handler);
            Check(await http.GetStringAsync($"http://127.0.0.1:{targetPort}/", ct) == "recovered", "Recovered proxy TCP payload failed");
            await serve;
            // Keep UDP association alive while transferring a SOCKS-framed datagram to a local echo server.
            using var control = new TcpClient(); await control.ConnectAsync(IPAddress.Loopback, port, ct);
            var stream = control.GetStream(); await stream.WriteAsync(new byte[] { 5, 1, 0 }, ct);
            byte[] greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, ct); Check(greeting.SequenceEqual(new byte[] { 5, 0 }), "SOCKS greeting failed");
            await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
            byte[] reply = new byte[10]; await stream.ReadExactlyAsync(reply, ct); Check(reply[1] == 0 && reply[3] == 1, "UDP association failed");
            int relayPort = (reply[8] << 8) | reply[9];
            using var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int echoPort = ((IPEndPoint)echo.Client.LocalEndPoint!).Port;
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            byte[] datagram = [0, 0, 0, 1, 127, 0, 0, 1, (byte)(echoPort >> 8), (byte)echoPort, 42];
            await udp.SendAsync(datagram, new IPEndPoint(IPAddress.Loopback, relayPort), ct);
            var incoming = await echo.ReceiveAsync(ct); Check(incoming.Buffer.SequenceEqual(new byte[] { 42 }), "Proxy UDP payload failed");
            await echo.SendAsync(incoming.Buffer, incoming.RemoteEndPoint, ct);
            Check((await udp.ReceiveAsync(ct)).Buffer.Last() == 42, "Proxy UDP return failed");
            byte[] query = [0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 8, (byte)'r', (byte)'e', (byte)'c', (byte)'o', (byte)'v', (byte)'e', (byte)'r', (byte)'y', 4, (byte)'t', (byte)'e', (byte)'s', (byte)'t', 0, 0, 1, 0, 1];
            using var dns = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await dns.SendAsync(query, new IPEndPoint(IPAddress.Parse("127.0.0.154"), dnsPort), ct);
            void CheckDns(byte[] packet) => Check(packet.Length > 12 && packet[0] == 0x12 && packet[1] == 0x34 && (packet[3] & 15) == 0 && packet.TakeLast(4).SequenceEqual(new byte[] { 192, 0, 2, 123 }), "Recovered DNS response failed");
            CheckDns((await dns.ReceiveAsync(ct)).Buffer);
            using var dnsTcp = new TcpClient(); await dnsTcp.ConnectAsync(IPAddress.Parse("127.0.0.154"), dnsPort, ct);
            await dnsTcp.GetStream().WriteAsync(new byte[] { 0, (byte)query.Length }.Concat(query).ToArray(), ct);
            byte[] length = new byte[2]; await dnsTcp.GetStream().ReadExactlyAsync(length, ct);
            byte[] answer = new byte[(length[0] << 8) | length[1]]; await dnsTcp.GetStream().ReadExactlyAsync(answer, ct); CheckDns(answer);
            log("PASS: recovered real Mihomo TCP HTTP, SOCKS UDP round-trip, DNS UDP and TCP on original ports");
        } finally {
            if (!core.HasExited) core.Kill(); await core.WaitForExitAsync(); await output;
            File.Delete(runtime); Check(ports.WaitForRelease(log), "Recovered proxy shutdown left ports occupied");
        }
    }
}
