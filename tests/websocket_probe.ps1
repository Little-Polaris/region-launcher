param([int]$ProxyPort = 0, [int]$IdleSeconds = 20)
$ErrorActionPreference = 'Stop'
$socket = [Net.WebSockets.ClientWebSocket]::new()
$socket.Options.Proxy = if ($ProxyPort) { [Net.WebProxy]::new("socks5://127.0.0.1:$ProxyPort") } else { $null }
$socket.Options.KeepAliveInterval = [TimeSpan]::FromSeconds(5)
$cancel = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($IdleSeconds + 45))
try {
    $null = $socket.ConnectAsync([Uri]'wss://ws.postman-echo.com/raw', $cancel.Token).GetAwaiter().GetResult()
    'WEBSOCKET_CONNECTED'
    foreach ($phase in @('first', 'after-idle')) {
        if ($phase -eq 'after-idle') { Start-Sleep -Seconds $IdleSeconds }
        $bytes = [Text.Encoding]::UTF8.GetBytes("RegionLauncher-WebSocket-test-$phase")
        $null = $socket.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text, $true, $cancel.Token).GetAwaiter().GetResult()
        $buffer = [byte[]]::new(4096)
        $message = [IO.MemoryStream]::new()
        try {
            do {
                $part = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $cancel.Token).GetAwaiter().GetResult()
                if ($part.MessageType -ne [Net.WebSockets.WebSocketMessageType]::Text) { throw 'Unexpected close or binary response' }
                $message.Write($buffer, 0, $part.Count)
                if ($message.Length -gt 4096) { throw 'Echo too large' }
            } until ($part.EndOfMessage)
            if ([Text.Encoding]::UTF8.GetString($message.ToArray()) -ne [Text.Encoding]::UTF8.GetString($bytes)) { throw 'Echo mismatch' }
        } finally { $message.Dispose() }
        "WEBSOCKET_ECHO_OK $phase"
    }
    $null = $socket.CloseAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'test complete', $cancel.Token).GetAwaiter().GetResult()
    'WEBSOCKET_CLOSE_OK'
} finally { $socket.Dispose(); $cancel.Dispose() }
