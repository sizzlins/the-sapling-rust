param(
    [Parameter(Position=0, ValueFromRemainingArguments=$true)]
    [string[]]$Command,
    [string]$HostAddr = "127.0.0.1",
    [int]$Port = 27016
)

# Client for the SaplingRustBridge read-only RCON (the retail game).
# Mirrors rcon.ps1 for the Rust port; the only difference is the default port.

$msg = ($Command -join " ").Trim()
if ([string]::IsNullOrWhiteSpace($msg)) {
    Write-Output "Usage: .\rcon_sapling.ps1 status"
    Write-Output "       .\rcon_sapling.ps1 species 50 20"
    Write-Output "       .\rcon_sapling.ps1 find ooga"
    exit 1
}

try {
    $tcp = New-Object System.Net.Sockets.TcpClient($HostAddr, $Port)
} catch {
    Write-Output "Cannot connect to ${HostAddr}:${Port} - is The Sapling running with SaplingRustBridge?"
    Write-Output $_.Exception.Message
    exit 1
}

try {
    $stream = $tcp.GetStream()
    $stream.ReadTimeout = 8000
    $reader = New-Object System.IO.StreamReader($stream)
    $writer = New-Object System.IO.StreamWriter($stream)
    $writer.AutoFlush = $true

    $banner = $reader.ReadLine()
    Write-Output $banner

    $writer.WriteLine($msg)

    # JSON replies are a single line; help is multi-line until the next prompt.
    $line = $reader.ReadLine()
    while ($null -ne $line) {
        Write-Output $line
        if ($line.StartsWith("{") -and $line.EndsWith("}")) { break }
        if ($line -eq "Bye.") { break }
        if (-not $reader.Peek()) { break }
        $line = $reader.ReadLine()
    }

    $writer.WriteLine("quit")
} finally {
    $tcp.Close()
}
