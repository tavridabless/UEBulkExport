$ErrorActionPreference = 'Stop'

$pipeName = $env:UEBE_KEY_PROBE_PIPE
$token = $env:UEBE_KEY_PROBE_TOKEN
if ([string]::IsNullOrWhiteSpace($pipeName) -or [string]::IsNullOrWhiteSpace($token)) {
    exit 10
}

$client = [System.IO.Pipes.NamedPipeClientStream]::new(
    '.', $pipeName, [System.IO.Pipes.PipeDirection]::Out)
try {
    $client.Connect(5000)
    $writer = [System.IO.StreamWriter]::new($client, [System.Text.Encoding]::ASCII, 1024, $true)
    try {
        $writer.NewLine = "`n"
        $writer.AutoFlush = $true
        $writer.WriteLine("HELLO|1|$token|$PID")
        $writer.WriteLine('KEY|12345678-1234-5678-9abc-def012345678|0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF')
        $writer.WriteLine('DONE')
    }
    finally {
        $writer.Dispose()
    }
}
finally {
    $client.Dispose()
}
