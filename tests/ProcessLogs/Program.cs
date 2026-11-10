using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

const string message = "Failed to open named pipe server process 32760: 拒绝访问。(0x5)";
if (args.Contains("--emit"))
{
    using var output = Console.OpenStandardOutput();
    byte[] raw = Encoding.UTF8.GetBytes(message + "\r\n日志结束🚀");
    foreach (byte value in raw) { output.WriteByte(value); output.Flush(); }
    using var error = Console.OpenStandardError();
    error.Write(Encoding.UTF8.GetBytes("错误：中文消息\n"));
    return;
}

// A child may split a Chinese character across reads, and different children
// may use different encodings on the inherited stream.
var local = CodePagesEncodingProvider.Instance.GetEncoding((int)Native.GetACP())!;
string prefix = new('x', 4095);
byte[] input = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(prefix + "中文🚀\r\n"),
    .. local.GetBytes(message + "\n"), .. Encoding.UTF8.GetBytes("进度\r完成\n末行")];
var lines = new List<string>();
await ProcessLog.ReadAsync(new FragmentedStream(input), lines.Add);
if (!lines.SequenceEqual(new[] { prefix + "中文🚀", local.GetString(local.GetBytes(message)), "进度", "完成", "末行" }))
    throw new Exception("UTF-8/local encoding, split multibyte character, CRLF or final line corrupted");

var psi = new ProcessStartInfo(Environment.ProcessPath!) {
    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
};
psi.ArgumentList.Add("--emit");
using var process = Process.Start(psi)!;
var stdout = new List<string>(); var stderr = new List<string>();
var reads = Task.WhenAll(ProcessLog.ReadAsync(process.StandardOutput.BaseStream, stdout.Add),
    ProcessLog.ReadAsync(process.StandardError.BaseStream, stderr.Add));
await process.WaitForExitAsync(); await reads;
if (process.ExitCode != 0 || !stdout.SequenceEqual(new[] { message, "日志结束🚀" }) ||
    !stderr.SequenceEqual(new[] { "错误：中文消息" })) throw new Exception("Redirected process logs corrupted");
Console.WriteLine($"PASS: UTF-8 and codepage {local.CodePage}; fragmented characters, BOM, CR/LF, final partial line; real child stdout/stderr");

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
}

static class Native
{
    [DllImport("kernel32.dll")]
    public static extern uint GetACP();
}
