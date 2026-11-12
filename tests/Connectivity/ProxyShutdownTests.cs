using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;

static class ProxyShutdownTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Set(DedicatedProxy proxy, string name, object? value) => typeof(DedicatedProxy).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(proxy, value);
    static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    public static async Task Run(string folder)
    {
        List<string> logs = [];
        void Log(string text) { lock(logs) logs.Add(text); }
        using var outside = new TcpListener(IPAddress.Loopback, 0); outside.Start();
        int outsidePort = ((IPEndPoint)outside.LocalEndpoint).Port;
        async Task OutsideWorks() {
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, outsidePort);
            using var accepted = await outside.AcceptTcpClientAsync();
            await accepted.GetStream().WriteAsync(new byte[] { 42 });
            var b = new byte[1]; Check(await client.GetStream().ReadAsync(b) == 1 && b[0] == 42, "Outside connection failed");
        }
        int port = FreePort(), dnsPort = FreePort();
        var dns = IPAddress.Parse("127.0.0.154");
        var ports = new ProxyPorts(port, dns, dnsPort);
        foreach (var endpoint in new[] { (IPAddress.Loopback,port,ProtocolType.Tcp), (IPAddress.Loopback,port,ProtocolType.Udp), (dns,dnsPort,ProtocolType.Tcp), (dns,dnsPort,ProtocolType.Udp) }) {
            using var foreign = new Socket(AddressFamily.InterNetwork, endpoint.Item3 == ProtocolType.Tcp ? SocketType.Stream : SocketType.Dgram, endpoint.Item3);
            foreign.ExclusiveAddressUse = true; foreign.Bind(new IPEndPoint(endpoint.Item1,endpoint.Item2));
            if (endpoint.Item3 == ProtocolType.Tcp) foreign.Listen();
            try { ports.RequireAvailable(Log); throw new Exception("Foreign reservation accepted"); }
            catch (IOException ex) { Check(ex.Message.Contains(endpoint.Item2.ToString()) && ex.InnerException is SocketException, "Endpoint/error missing"); }
            Check(foreign.IsBound, "Foreign socket closed");
        }
        Check(logs.Any(s => s.Contains($"PID={Environment.ProcessId}")) && logs.Any(s => s.Contains("状态=Listen")), "Owner/state diagnostics missing");
        await OutsideWorks();
        // A foreign process can own a port after the owned process has exited. Never kill it.
        using (var dummy = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--hold") { UseShellExecute=false, CreateNoWindow=true })!)
        using (var proxy = new DedicatedProxy(Log)) {
            Set(proxy,"process",dummy); Set(proxy,"ports",new ProxyPorts(outsidePort,dns,dnsPort));
            Check(!await proxy.StopAsync(), "Claimed release while foreign socket still bound");
            Check(!proxy.CleanupComplete,"Busy ports reported complete");
            await OutsideWorks();
            Check(logs.Any(s=>s.Contains("[端口释放] 未完成")), "Release failure not recorded");
            Set(proxy,"ports",null);
        }
        string engine = Environment.GetEnvironmentVariable("REGION_TEST_ENGINE") ?? throw new Exception("Set REGION_TEST_ENGINE to test the real proxy lifecycle");
        var settings = new JsonObject {
            ["mixed-port"]=port,["bind-address"]="127.0.0.1",["allow-lan"]=false,["ipv6"]=false,["log-level"]="warning",
            ["dns"]=new JsonObject { ["enable"]=true,["listen"]=$"{dns}:{dnsPort}",["ipv6"]=false,["nameserver"]=new JsonArray("1.1.1.1") },
            ["rules"]=new JsonArray("MATCH,DIRECT")
        };
        for (int i=0;i<3;i++) {
            ports.RequireAvailable(Log);
            string runtime = Path.Combine(folder,$"shutdown-runtime-{i}.json");
            await File.WriteAllTextAsync(runtime,settings.ToJsonString());
            var psi = new ProcessStartInfo(engine) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"-d",folder,"-f",runtime}) psi.ArgumentList.Add(arg);
            using var owned = Process.Start(psi)!;
            var drain = Task.WhenAll(owned.StandardOutput.ReadToEndAsync(),owned.StandardError.ReadToEndAsync());
            using var proxy = new DedicatedProxy(Log);
            Set(proxy,"process",owned); Set(proxy,"ports",ports); Set(proxy,"runtimeConfig",runtime); Set(proxy,"output",drain);
            {
                bool ready = false;
                for(int attempt=0;attempt<60;attempt++) {
                    Check(!owned.HasExited,"Real core exited before ready");
                    try { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback,port); ready=true;break; }
                    catch(SocketException) {await Task.Delay(100);}
                }
                Check(ready,"Core not ready");
                using var held = new TcpClient(); await held.ConnectAsync(IPAddress.Loopback,port);
                await OutsideWorks();
                var watch = Stopwatch.StartNew();
                Check(await proxy.StopAsync(),"Real core ports did not release");
                Check(proxy.CleanupComplete,"Released core reported incomplete");
                Check(watch.Elapsed < TimeSpan.FromSeconds(8),"Shutdown exceeded test bound");
                Check(!File.Exists(runtime),"Private runtime not removed after exit");
                ports.RequireAvailable(Log); await OutsideWorks();
                Check(await proxy.StopAsync(),"Repeated stop not idempotent");
            }
        }
        lock(logs) File.WriteAllLines(Path.Combine(folder,"proxy-shutdown.log"),logs);
        Console.WriteLine("PASS: real Mihomo start/stop/rebind x3, active sockets, TCP/UDP conflict diagnostics, foreign owner survival, idempotent cleanup");
    }
}
