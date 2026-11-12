using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;

static class ProxyOwnershipTests
{
    public static async Task Run(string config)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        Process Hold() => Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--hold") { UseShellExecute = false, CreateNoWindow = true })!;
        using var owned = Hold(); using var outside = Hold();
        using var proxy = new DedicatedProxy(configPath: config);
        byte[] original = File.ReadAllBytes(config);
        typeof(DedicatedProxy).GetField("process", fields)!.SetValue(proxy, owned);
        typeof(DedicatedProxy).GetField("fingerprint", fields)!.SetValue(proxy, Convert.ToHexString(SHA256.HashData(original)));
        Socket? reservation = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try {
            await proxy.EnsureStarted();
            if (owned.HasExited || outside.HasExited) throw new Exception("Healthy ownership check stopped a process");
            using (var canceled = new CancellationTokenSource()) {
                canceled.Cancel();
                try { await proxy.RestartAsync(canceled.Token); throw new Exception("Canceled restart accepted"); } catch (OperationCanceledException) { }
                if (owned.HasExited) throw new Exception("Canceled restart killed its core");
            }
            File.AppendAllText(config, " ");
            try { await proxy.RestartAsync(); throw new Exception("Configuration changed during recovery"); } catch (IOException) { }
            if (owned.HasExited) throw new Exception("Configuration rejection stopped the original core");
            File.WriteAllBytes(config, original);
            // Occupied DNS must refuse a replacement, never adopt/kill the port's owner.
            try { reservation.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.153"), 53)); }
            catch (SocketException) { reservation.Dispose(); reservation = null; }
            int ownedPid = owned.Id;
            try { await proxy.RestartAsync(); throw new Exception("Occupied DNS was accepted"); } catch (IOException) { }
            try { using var leftover = Process.GetProcessById(ownedPid); if (!leftover.HasExited) throw new Exception("Owned core survived restart"); }
            catch (ArgumentException) { }
            if (outside.HasExited) throw new Exception("Recovery stopped an outside process");
            if (reservation != null && !reservation.IsBound) throw new Exception("Recovery released someone else's listener");
            Console.WriteLine("PASS: owned process lifecycle, canceled restart, configuration pinning, occupied-listener refusal and outside process survival");
        } finally {
            reservation?.Dispose(); File.WriteAllBytes(config, original);
            proxy.Dispose();
            if (!outside.HasExited) { outside.Kill(); await outside.WaitForExitAsync(); }
        }
    }
}
