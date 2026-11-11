// Invoked only by the check button or launch preparation; no periodic probe.
sealed class ProxyRecovery(
    Func<CancellationToken, Task> ensureStarted,
    Func<CancellationToken, Task> restart,
    Func<CancellationToken, Task<ProbeResult[]>> probe,
    Action<string> log,
    TimeProvider? clock = null,
    TimeSpan? retryDelay = null,
    TimeSpan? restartCooldown = null)
{
    readonly SemaphoreSlim gate = new(1);
    readonly TimeProvider time = clock ?? TimeProvider.System;
    long? lastRestart;

    public async Task<ProbeResult[]> CheckAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try {
            cancellationToken.ThrowIfCancellationRequested();
            await ensureStarted(cancellationToken);
            var results = await probe(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Any(r => r.Ok)) return results;

            log("[代理修复] 实际联网检测全部失败，稍后复查，排除短暂抖动或检测站点异常。");
            await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(2), cancellationToken);
            results = await probe(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Any(r => r.Ok)) { log("[代理修复] 复查已恢复，无需重启代理。"); return results; }

            var cooldown = restartCooldown ?? TimeSpan.FromMinutes(1);
            if (lastRestart is long previous && time.GetElapsedTime(previous) < cooldown)
                throw new IOException("实际联网仍失败；距上次重连不足一分钟，暂不重复重启。已有程序保持运行，请稍后再次检测。");
            lastRestart = time.GetTimestamp();
            log("[代理修复] 连续两轮实际联网失败，重连独立代理；保留已启动的软件和网络隔离。现有网络连接可能中断，由软件重新建立。");
            await restart(cancellationToken);
            results = await probe(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!results.Any(r => r.Ok))
                throw new IOException("代理已重连，但实际联网仍失败；已有程序保持运行，新程序暂不启动。请检查节点或网络，并查看日志。");
            log("[代理修复] 重连后实际联网验证通过，已有程序可重新建立连接。");
            return results;
        } finally { gate.Release(); }
    }
}
