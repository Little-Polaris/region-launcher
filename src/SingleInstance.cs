using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

static class SingleInstance
{
    /// <summary>Request byte 2 on the pipe asks the running launcher to exit (sent by the installer and uninstaller).</summary>
    public static Action<Form>? ExitRequested { get; set; }

    public static void Run(Func<Form> createWindow, Action<Form> restoreWindow, string? testScope = null)
    {
        string name = testScope ?? $"RegionLauncher.UI.{WindowsIdentity.GetCurrent().User!.Value}.{Process.GetCurrentProcess().SessionId}";
        using var mutex = new Mutex(false, @"Local\" + name);
        bool owned = false;
        try {
            var timeout = Stopwatch.StartNew();
            while (!owned) {
                try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                if (owned) break;
                try {
                    using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    client.Connect(250);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    byte[] pid = new byte[4];
                    client.ReadExactlyAsync(pid, deadline.Token).AsTask().GetAwaiter().GetResult();
                    AllowSetForegroundWindow(BitConverter.ToInt32(pid));
                    client.WriteAsync(new byte[] { 1 }, deadline.Token).AsTask().GetAwaiter().GetResult();
                    return;
                } catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) {
                    if (timeout.Elapsed > TimeSpan.FromSeconds(10)) {
                        MessageBox.Show("启动器已在运行，但暂时无法唤出窗口。请稍后重试，或从托盘打开。", "Region Launcher");
                        return;
                    }
                }
            }
            using var window = createWindow();
            _ = window.Handle;
            using var stop = new CancellationTokenSource();
            Task listener = ListenAsync(name, window, restoreWindow, stop.Token);
            try { Application.Run(window); }
            finally { stop.Cancel(); listener.GetAwaiter().GetResult(); }
        } finally { if (owned) mutex.ReleaseMutex(); }
    }

    static async Task ListenAsync(string name, Form window, Action<Form> restore, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested) {
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try {
                await server.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                await server.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), deadline.Token).ConfigureAwait(false);
                byte[] request = new byte[1];
                await server.ReadExactlyAsync(request, deadline.Token).ConfigureAwait(false);
                if (request[0] == 1 && !stop.IsCancellationRequested)
                    window.BeginInvoke(() => { if (!window.IsDisposed) restore(window); });
                else if (request[0] == 2 && !stop.IsCancellationRequested && ExitRequested is { } exit)
                    window.BeginInvoke(() => { if (!window.IsDisposed) exit(window); });
            } catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) {
                // A duplicate may exit mid-request, or the main window may already be closing.
            }
        }
    }

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
}
