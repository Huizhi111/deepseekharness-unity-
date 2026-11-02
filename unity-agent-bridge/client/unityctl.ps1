<#
.SYNOPSIS
    Unity Agent Bridge 命令行客户端（Windows PowerShell 5.1+ / PowerShell 7）。

.DESCRIPTION
    通过本机 HTTP 接口调用 Unity 编辑器里的插件。
    默认端口 17899，令牌自动从 <项目>/Library/UnityAgentBridge/token.txt 读取。
    传输层直接用 TcpClient，避免 PowerShell 的 HTTP 客户端在回环地址上的各种怪毛病。

.EXAMPLE
    .\unityctl.ps1 ping

.EXAMPLE
    .\unityctl.ps1 hierarchy -ArgsJson '{"depth":2}'

.EXAMPLE
    .\unityctl.ps1 go.create -ArgsJson '{"type":"cube","name":"Box","position":[0,1,0]}'

.EXAMPLE
    .\unityctl.ps1 exec "return 6*7;"

.EXAMPLE
    .\unityctl.ps1 cs.execute -ArgsJson '{"code":"return 1+2;"}' -Wait
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command = "ping",

    [Parameter(Position = 1)]
    [string]$ArgsJson,

    [string]$ArgsFile,

    [int]$Port = 0,
    [string]$Token = "",
    [string]$Project = "",
    [int]$TimeoutMs = 120000,
    [switch]$Wait,
    [switch]$Compact,
    [switch]$Raw
)

$ErrorActionPreference = "Stop"

function Resolve-ProjectDir {
    param([string]$Explicit)

    if ($Explicit) { return (Resolve-Path -LiteralPath $Explicit).Path }

    $candidates = @()
    if ($env:UNITY_BRIDGE_PROJECT) { $candidates += $env:UNITY_BRIDGE_PROJECT }
    $candidates += (Get-Location).Path

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate "Library\UnityAgentBridge"))) {
            return $candidate
        }
    }
    return $null
}

function Read-BridgeSettings {
    param([string]$ProjectDir)

    $settings = @{ port = 17899; token = "" }
    if (-not $ProjectDir) { return $settings }

    $base = Join-Path $ProjectDir "Library\UnityAgentBridge"
    $settingsFile = Join-Path $base "settings.json"
    if (Test-Path -LiteralPath $settingsFile) {
        try {
            $json = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
            if ($json.port) { $settings.port = [int]$json.port }
            if ($json.token) { $settings.token = [string]$json.token }
        }
        catch { }
    }

    $tokenFile = Join-Path $base "token.txt"
    if (Test-Path -LiteralPath $tokenFile) {
        $fileToken = (Get-Content -LiteralPath $tokenFile -Raw).Trim()
        if ($fileToken) { $settings.token = $fileToken }
    }
    return $settings
}

function Send-BridgeCommand {
    param(
        [string]$Cmd,
        [object]$Arguments,
        [int]$EffectivePort,
        [string]$EffectiveToken,
        [int]$TimeoutMs
    )

    $payload = @{ cmd = $Cmd; timeoutMs = $TimeoutMs }
    if ($null -ne $Arguments) { $payload["args"] = $Arguments }

    $body = $payload | ConvertTo-Json -Depth 30 -Compress
    $bodyBytes = [Text.Encoding]::UTF8.GetBytes($body)

    $head = "POST /cmd HTTP/1.1`r`n" +
            "Host: 127.0.0.1:$EffectivePort`r`n" +
            "X-Bridge-Token: $EffectiveToken`r`n" +
            "Content-Type: application/json; charset=utf-8`r`n" +
            "Content-Length: $($bodyBytes.Length)`r`n" +
            "Connection: close`r`n`r`n"
    $headBytes = [Text.Encoding]::ASCII.GetBytes($head)

    $lastError = "unknown"
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $async = $client.BeginConnect("127.0.0.1", $EffectivePort, $null, $null)
            if (-not $async.AsyncWaitHandle.WaitOne(3000)) { throw "连接 $EffectivePort 端口超时" }
            $client.EndConnect($async)

            $client.ReceiveTimeout = [Math]::Max(5000, $TimeoutMs + 15000)
            $stream = $client.GetStream()
            $stream.Write($headBytes, 0, $headBytes.Length)
            $stream.Write($bodyBytes, 0, $bodyBytes.Length)
            $stream.Flush()

            $memory = New-Object System.IO.MemoryStream
            $buffer = New-Object byte[] 8192
            while ($true) {
                $read = $stream.Read($buffer, 0, $buffer.Length)
                if ($read -le 0) { break }
                $memory.Write($buffer, 0, $read)
            }

            $raw = [Text.Encoding]::UTF8.GetString($memory.ToArray())
            $split = $raw.IndexOf("`r`n`r`n")
            $content = if ($split -ge 0) { $raw.Substring($split + 4) } else { $raw }
            $status = 0
            if ($split -gt 0) {
                $statusLine = ($raw.Substring(0, $split) -split "`r`n")[0]
                $parts = $statusLine.Split(' ')
                if ($parts.Length -ge 2) {
                    $parsed = 0
                    [void][int]::TryParse($parts[1], [ref]$parsed)
                    $status = $parsed
                }
            }
            return @{ Ok = $true; Status = $status; Content = $content }
        }
        catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Milliseconds 250
        }
        finally {
            try { $client.Close() } catch { }
        }
    }

    return @{ Ok = $false; Status = 0; Content = $lastError }
}

function Show-Result {
    param([string]$Json, [int]$Status)

    if ($Raw) { Write-Output $Json; return }

    try {
        $parsed = $Json | ConvertFrom-Json
    }
    catch {
        Write-Output $Json
        return
    }

    if ($parsed.PSObject.Properties["ok"] -and -not $parsed.ok) {
        $code = if ($parsed.PSObject.Properties["code"]) { $parsed.code } else { "error" }
        Write-Host ("[失败] " + $parsed.error + "  (" + $code + ", HTTP " + $Status + ")") -ForegroundColor Red
        return
    }

    if ($Compact) {
        $Json
    }
    else {
        $Json | ConvertFrom-Json | ConvertTo-Json -Depth 30
    }
}

# ------------------------------------------------------------------ resolve

$projectDir = Resolve-ProjectDir -Explicit $Project
$settings = Read-BridgeSettings -ProjectDir $projectDir

if ($Port -le 0) { $Port = $settings.port }
if (-not $Token) {
    if ($env:UNITY_BRIDGE_TOKEN) { $Token = $env:UNITY_BRIDGE_TOKEN }
    else { $Token = $settings.token }
}

if ($Command -eq "help" -or $Command -eq "commands") { $Command = "commands.list" }
if ($Command -eq "exec") {
    $ArgsJson = (@{ code = $ArgsJson } | ConvertTo-Json -Depth 5 -Compress)
    $Command = "cs.execute"
    $Wait = $true
}

if ($ArgsFile) {
    if (-not (Test-Path -LiteralPath $ArgsFile)) {
        Write-Host "[失败] 找不到参数文件: $ArgsFile" -ForegroundColor Red
        exit 2
    }
    $ArgsJson = Get-Content -LiteralPath $ArgsFile -Raw
}

$argsObject = $null
if ($ArgsJson) {
    try {
        $argsObject = $ArgsJson | ConvertFrom-Json
    }
    catch {
        Write-Host "[失败] -ArgsJson 不是合法 JSON: $($_.Exception.Message)" -ForegroundColor Red
        exit 2
    }
}

# -------------------------------------------------------------------- send

$result = Send-BridgeCommand -Cmd $Command -Arguments $argsObject -EffectivePort $Port -EffectiveToken $Token -TimeoutMs $TimeoutMs

if (-not $result.Ok) {
    Write-Host ("[连接失败] " + $result.Content) -ForegroundColor Red
    Write-Host "确认 Unity 已打开、Tools/Unity Agent Bridge 里服务已启动、端口为 $Port。" -ForegroundColor Yellow
    exit 2
}

$exitCode = 0
try {
    $parsed = $result.Content | ConvertFrom-Json
    if ($parsed.PSObject.Properties["ok"] -and -not $parsed.ok) { $exitCode = 1 }
}
catch { }

Show-Result -Json $result.Content -Status $result.Status

# ------------------------------------------------------------- optional wait

if ($Wait -and $Command -eq "cs.execute" -and $exitCode -eq 0) {
    $parsed = $result.Content | ConvertFrom-Json
    $jobId = $parsed.result.jobId
    if ($jobId) {
        Write-Host "[等待编译并执行] job $jobId ..." -ForegroundColor DarkGray
        $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 800
            $poll = Send-BridgeCommand -Cmd "cs.status" -Arguments @{ jobId = $jobId } -EffectivePort $Port -EffectiveToken $Token -TimeoutMs 30000
            if (-not $poll.Ok) {
                Write-Host "[连接中断] Unity 正在重载程序集，稍后重试..." -ForegroundColor DarkGray
                continue
            }
            try { $status = $poll.Content | ConvertFrom-Json } catch { continue }
            if (-not $status.result) { continue }
            if ($status.result.status -eq "pending") { continue }

            Show-Result -Json $poll.Content -Status $poll.Status
            if (-not $status.result.ok) { $exitCode = 1 }
            break
        }
    }
}

exit $exitCode
