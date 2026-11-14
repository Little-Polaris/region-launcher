using System.Net;
using System.Net.Sockets;

static class RecoveryTests
{
    public static async Task Run(ProbeResult good)
    {
        var bad = good with { Ok = false, Ip = null, Error = "timeout" };
        void Check(bool ok, string error) { if (!ok) throw new Exception(error); }
        async Task Case(string name, ProbeResult[][] rounds, int expectedRestarts, bool fails = false) {
            int probes = 0, restarts = 0;
            var recovery = new ProxyRecovery(_ => Task.CompletedTask,
                _ => { restarts++; return Task.CompletedTask; },
                _ => Task.FromResult(rounds[probes++]), _ => {}, retryDelay: TimeSpan.Zero);
            try { await recovery.CheckAsync(default); Check(!fails, name + " accepted failed internet"); }
            catch (IOException) when (fails) { }
            Check(probes == rounds.Length && restarts == expectedRestarts, name + " wrong recovery count");
            Check(recovery.Recovered == (!fails && rounds.Length > 1), name + " wrong recovery outcome");
        }
        await Case("healthy", [[good]], 0);
        await Case("one target unavailable", [[bad, good]], 0);
        await Case("transient failure", [[bad], [good]], 0);
        await Case("recovered", [[bad], [bad], [good]], 1);
        await Case("node remains unavailable", [[bad], [bad], [bad]], 1, true);

        int restarts = 0, probes = 0;
        var clock = new TestClock();
        var failed = new ProxyRecovery(_ => Task.CompletedTask,
            _ => { restarts++; return Task.CompletedTask; },
            _ => { probes++; return Task.FromResult(new[] { bad }); }, _ => {}, clock, TimeSpan.Zero);
        for (int i = 0; i < 3; i++) {
            if (i == 2) clock.Advance(TimeSpan.FromMinutes(2));
            try { await failed.CheckAsync(default); throw new Exception("Failed node accepted"); } catch (IOException) { }
            Check(restarts == (i == 2 ? 2 : 1), "Repeated clicks bypass cooldown or never recover later");
        }
        Check(probes == 8, "Cooldown must still check real connectivity");

        using (var cancel = new CancellationTokenSource()) {
            var canceled = new ProxyRecovery(_ => Task.CompletedTask, _ => { throw new Exception("Restart after cancellation"); },
                _ => { cancel.Cancel(); return Task.FromResult(new[] { bad }); }, _ => {}, retryDelay: TimeSpan.Zero);
            try { await canceled.CheckAsync(cancel.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
        }
        int active = 0, concurrentRestarts = 0;
        var serial = new ProxyRecovery(_ => Task.CompletedTask,
            _ => { concurrentRestarts++; return Task.CompletedTask; }, async ct => {
                Check(Interlocked.Increment(ref active) == 1, "Recovery requests overlap");
                await Task.Delay(10, ct); Interlocked.Decrement(ref active);
                return new[] { concurrentRestarts == 0 ? bad : good };
            }, _ => {}, retryDelay: TimeSpan.Zero);
        await Task.WhenAll(serial.CheckAsync(default), serial.CheckAsync(default));
        Check(concurrentRestarts == 1, "Concurrent requests restart twice");
        Check(!serial.Recovered, "Healthy follow-up retained previous recovery outcome");

        // A successful SOCKS CONNECT with no HTTP payload must trigger recovery.
        using var direct = new TcpListener(IPAddress.Loopback, 0); direct.Start();
        string url = $"http://127.0.0.1:{((IPEndPoint)direct.LocalEndpoint).Port}/trace";
        int fixedCount = 0, requests = 0;
        var stalled = new ProxyRecovery(_ => Task.CompletedTask,
            _ => { fixedCount++; return Task.CompletedTask; }, async ct => {
                requests++;
                using var socks = new FakeSocks(fixedCount == 0 ? "stall" : "success");
                var results = await ProxyHealth.ProbeAsync(socks.Port, _ => {}, [new("Fixture", url, true)], TimeSpan.FromMilliseconds(250), ct);
                Check(socks.Handshake && !direct.Pending(), "Handshake missing or direct fallback"); return results;
            }, _ => {}, retryDelay: TimeSpan.Zero);
        var recovered = await stalled.CheckAsync(default);
        Check(recovered[0].Ok && fixedCount == 1 && requests == 3, "Stalled payload was not recovered");
        Console.WriteLine("PASS: on-demand recovery, healthy no-op, transient failures, persistent failures, cooldown, cancellation, serialization and handshake-success/payload-stall recovery without direct fallback");
    }
    sealed class TestClock : TimeProvider
    {
        long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan value) => ticks += value.Ticks;
    }
}
