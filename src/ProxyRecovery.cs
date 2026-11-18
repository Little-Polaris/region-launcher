// Invoked only by the check button or launch preparation; no periodic probe.
sealed class ProxyRecovery(
    Func<CancellationToken, Task> ensureStarted,
    Func<CancellationToken, Task> reconnect,
    Func<CancellationToken, Task<ProbeResult[]>> probe,
    Action<string> log,
    TimeProvider? clock = null,
    TimeSpan? retryDelay = null,
    TimeSpan? reconnectCooldown = null)
{
    readonly SemaphoreSlim gate = new(1);
    readonly TimeProvider time = clock ?? TimeProvider.System;
    long? lastReconnect;
    public bool Recovered { get; private set; }

    public async Task<ProbeResult[]> CheckAsync(CancellationToken cancellationToken, bool reconnectOnProbeFailure = true)
    {
        await gate.WaitAsync(cancellationToken);
        try {
            Recovered = false;
            cancellationToken.ThrowIfCancellationRequested();
            await ensureStarted(cancellationToken);
            var results = await probe(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Any(r => r.Ok)) return results;

            log("[代理修复] 实际联网检测全部失败，稍后复查，排除短暂抖动或检测站点异常。");
            await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(2), cancellationToken);
            results = await probe(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Any(r => r.Ok)) { Recovered = true; log("[代理修复] 复查已恢复。"); return results; }

            if (!reconnectOnProbeFailure)
                throw new IOException("自动故障切换组暂未通过实际联网检测。Mihomo 会继续检查节点；已有程序保持运行，新程序暂不启动。请查看日志确认节点、DNS 或上游网络状态。");

            var cooldown = reconnectCooldown ?? TimeSpan.FromMinutes(1);
            if (lastReconnect is long previous && time.GetElapsedTime(previous) < cooldown)
                throw new IOException("实际联网仍失败；距上次重连不足一分钟，暂不重复重连。已有程序保持运行，请稍后再次检测。");
            lastReconnect = time.GetTimestamp();
            log("[代理修复] 连续两轮实际联网失败，在现有内核中重连节点。已启动的软件可重新建立连接。");
            await reconnect(cancellationToken);
            results = await probe(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!results.Any(r => r.Ok))
                throw new IOException("代理已重连，但实际联网仍失败；已有程序保持运行，新程序暂不启动。请检查节点或网络，并查看日志。");
            log("[代理修复] 重连后实际联网验证通过，已有程序可重新建立连接。");
            Recovered = true;
            return results;
        } finally { gate.Release(); }
    }
}
