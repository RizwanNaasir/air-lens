<#
.SYNOPSIS
  Minimal AirLens protocol client: connects, handshakes, keeps the session alive and measures the
  stream. Optionally saves the raw Annex-B elementary stream for ffprobe/ffplay.

.EXAMPLE
  adb forward tcp:47474 tcp:47474
  .\tools\probe-stream.ps1 -Seconds 10 -Out stream.h264
  ffprobe -hide_banner stream.h264

.NOTES
  Speaks docs/PROTOCOL.md v1. Windows PowerShell 5.1 compatible; no dependencies.
#>
param(
    [string]$HostName = "127.0.0.1",
    [int]$Port = 47474,
    [int]$Seconds = 10,
    [string]$Out = ""
)
$ErrorActionPreference = "Stop"

# Unsigned 32-bit read as int64: PowerShell refuses to narrow values >= 2^31 to [int].
function Get-BE32([byte[]]$b, [int]$o) {
    return ([int64]$b[$o] -shl 24) -bor ([int64]$b[$o + 1] -shl 16) -bor ([int64]$b[$o + 2] -shl 8) -bor [int64]$b[$o + 3]
}
function Get-BE16([byte[]]$b, [int]$o) { return ([int]$b[$o] -shl 8) -bor [int]$b[$o + 1] }
function Get-BE64([byte[]]$b, [int]$o) { return ((Get-BE32 $b $o) -shl 32) -bor (Get-BE32 $b ($o + 4)) }
function Set-BE64([byte[]]$b, [int]$o, [int64]$v) { for ($i = 0; $i -lt 8; $i++) { $b[$o + $i] = [byte](($v -shr (56 - 8 * $i)) -band 0xFF) } }
function Set-BE32([byte[]]$b, [int]$o, [int64]$v) { for ($i = 0; $i -lt 4; $i++) { $b[$o + $i] = [byte](($v -shr (24 - 8 * $i)) -band 0xFF) } }

$client = New-Object System.Net.Sockets.TcpClient
$client.NoDelay = $true
$client.ReceiveBufferSize = 8MB
$client.Connect($HostName, $Port)
$stream = $client.GetStream()
$stream.ReadTimeout = 3000

function Read-Exactly([int]$n) {
    $buf = New-Object byte[] $n
    $off = 0
    while ($off -lt $n) {
        $r = $stream.Read($buf, $off, $n - $off)
        if ($r -le 0) { throw "server closed the connection" }
        $off += $r
    }
    return , $buf
}

# HELLO: header(type 0x80, len 8) + magic "ALNS" + version 1 + caps (HEVC | 4K)
$hello = [byte[]](0x80, 0, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x41, 0x4C, 0x4E, 0x53, 0, 1, 0, 3)
$stream.Write($hello, 0, $hello.Length)

$file = if ($Out) { [System.IO.File]::Create($Out) } else { $null }
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$lastPing = -1000; $lastReport = 0
$frames = 0; $keys = 0; $configs = 0; $bytes = 0L; $discont = 0; $firstFrameMs = -1
$lastSeq = $null; $seqGaps = 0; $rtts = New-Object System.Collections.Generic.List[double]
$firstPts = $null; $lastPts = 0L; $maxFrame = 0

Write-Host "connected to ${HostName}:$Port"
try {
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        $now = $clock.ElapsedMilliseconds
        if ($now - $lastPing -ge 500) {
            $ping = New-Object byte[] 20; $ping[0] = 0x82
            Set-BE64 $ping 12 ([int64]($clock.Elapsed.TotalMilliseconds * 1000))
            $stream.Write($ping, 0, 20); $lastPing = $now
        }
        if ($now - $lastReport -ge 1000) {
            $rep = New-Object byte[] 32; $rep[0] = 0x83; $rep[7] = 12
            Set-BE32 $rep 20 $frames
            $stream.Write($rep, 0, 32); $lastReport = $now
        }
        $h = Read-Exactly 20
        $type = $h[0]; $flags = $h[1]; $len = [int](Get-BE32 $h 4); $seq = Get-BE32 $h 8; $ts = Get-BE64 $h 12
        if ($len -gt 16MB) { throw "payload length $len exceeds MAX_PAYLOAD" }
        $p = if ($len -gt 0) { Read-Exactly $len } else { [byte[]]@() }
        switch ($type) {
            1 {
                $codec = if ($p[0] -eq 2) { "H.265" } else { "H.264" }
                Write-Host ("STREAM_INFO {0} {1}x{2}@{3} {4:N1} Mbps rotation={5} mirror={6} epoch={7}" -f $codec,
                    (Get-BE16 $p 2), (Get-BE16 $p 4), (Get-BE16 $p 6), ((Get-BE32 $p 8) / 1e6), (Get-BE16 $p 12), (($p[1] -band 2) -ne 0), (Get-BE32 $p 16))
            }
            2 {
                if ($file) { $file.Write($p, 0, $len) }
                # Config packets consume sequence numbers too, so track them before skipping.
                if ($null -ne $lastSeq -and $seq -ne (($lastSeq + 1) -band 0xFFFFFFFFL)) { $seqGaps++ }
                $lastSeq = $seq
                if ($flags -band 2) { $configs++; continue }
                if ($firstFrameMs -lt 0) { $firstFrameMs = $clock.ElapsedMilliseconds }
                $frames++; $bytes += $len; if ($len -gt $maxFrame) { $maxFrame = $len }
                if ($flags -band 1) { $keys++ }
                if ($flags -band 4) { $discont++ }
                if ($null -eq $firstPts) { $firstPts = $ts }; $lastPts = $ts
            }
            3 { $rtts.Add(($clock.Elapsed.TotalMilliseconds * 1000 - $ts - ((Get-BE64 $p 8) - (Get-BE64 $p 0))) / 1000.0) }
            4 { }
        }
    }
} finally {
    if ($file) { $file.Close() }
    $client.Close()
}

$span = if ($frames -gt 1) { ($lastPts - $firstPts) / 1e6 } else { 0 }
$fps = if ($span -gt 0) { ($frames - 1) / $span } else { 0 }
$mbps = if ($span -gt 0) { $bytes * 8 / $span / 1e6 } else { 0 }
$rttText = if ($rtts.Count) { "{0:N1} ms median" -f (($rtts | Sort-Object)[[int]($rtts.Count / 2)]) } else { "n/a" }
Write-Host ("frames={0} keyframes={1} config={2} discontinuities={3} seqGaps={4}" -f $frames, $keys, $configs, $discont, $seqGaps)
Write-Host ("first frame after {0} ms; measured {1:N2} fps, {2:N1} Mbps, largest frame {3:N0} KB; RTT {4}" -f $firstFrameMs, $fps, $mbps, ($maxFrame / 1KB), $rttText)
if ($Out) { Write-Host "saved $Out" }
