using System.Runtime.InteropServices;
using System.Text;

static class ProcessLog
{
    [DllImport("kernel32.dll")]
    static extern uint GetACP();
    static readonly Encoding utf8 = new UTF8Encoding(false, true);
    static readonly Encoding local = CodePagesEncodingProvider.Instance.GetEncoding((int)GetACP()) ?? Encoding.UTF8;

    static string Decode(byte[] bytes)
    {
        // A shared pipe can contain UTF-8 and legacy output from different children.
        try { return utf8.GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { return local.GetString(bytes); }
    }

    public static async Task ReadAsync(Stream stream, Action<string> append)
    {
        byte[] buffer = new byte[4096];
        using var line = new MemoryStream();
        bool afterCr = false;
        int count;
        while ((count = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            for (int i = 0; i < count; ++i)
            {
                byte value = buffer[i];
                if (value == '\n' && afterCr) { afterCr = false; continue; }
                afterCr = value == '\r';
                if (value == '\r' || value == '\n')
                {
                    append(Decode(line.ToArray())); line.SetLength(0);
                }
                else line.WriteByte(value);
            }
        }
        if (line.Length != 0) append(Decode(line.ToArray()));
    }
}
