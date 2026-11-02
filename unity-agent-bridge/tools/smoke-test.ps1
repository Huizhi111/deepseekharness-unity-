<#
.SYNOPSIS
    对一个已经在运行的 Unity + UnityAgentBridge 做冒烟测试（真实 HTTP 调用）。

.EXAMPLE
    .\smoke-test.ps1 -Project "D:\MyGame"

.EXAMPLE
    .\smoke-test.ps1 -Port 17899 -Token abc123
#>
[CmdletBinding()]
param(
    [string]$Project = "",
    [int]$Port = 0,
    [string]$Token = ""
)

$ErrorActionPreference = "Stop"

# 访问本机回环地址时不要走系统代理（否则首次请求可能被代理自动检测拖垮）
try { [System.Net.WebRequest]::DefaultWebProxy = $null } catch { }
# 关掉 Expect: 100-continue，避免每个请求多等 350ms
try { [System.Net.ServicePointManager]::Expect100Continue = $false } catch { }

function Resolve-Settings {
    if (-not $Project) {
        $candidate = (Get-Location).Path
        if (Test-Path (Join-Path $candidate "Library\UnityAgentBridge")) { $Project = $candidate }
    }
    if ($Project) {
        $base = Join-Path $Project "Library\UnityAgentBridge"
        $settingsFile = Join-Path $base "settings.json"
        if ((-not $Port) -and (Test-Path $settingsFile)) {
            $Port = [int]((Get-Content $settingsFile -Raw | ConvertFrom-Json).port)
        }
        $tokenFile = Join-Path $base "token.txt"
        if ((-not $Token) -and (Test-Path $tokenFile)) {
            $Token = (Get-Content $tokenFile -Raw).Trim()
        }
    }
    if (-not $Port) { $Port = 17899 }
}

Resolve-Settings

$script:Passed = 0
$script:Failed = 0
$script:Failures = New-Object System.Collections.Generic.List[string]

function Send-Cmd {
    param([string]$Cmd, [hashtable]$Arguments, [string]$UseToken)
    if (-not $PSBoundParameters.ContainsKey("UseToken")) { $UseToken = $Token }

    $payload = @{ cmd = $Cmd; timeoutMs = 60000 }
    if ($Arguments) { $payload["args"] = $Arguments }
    $body = $payload | ConvertTo-Json -Depth 20 -Compress
    $headers = @{}
    if ($UseToken) { $headers["X-Bridge-Token"] = $UseToken }

    try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/cmd" -Method Post `
            -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType "application/json; charset=utf-8" `
            -Headers $headers -UseBasicParsing -TimeoutSec 90
        return @{ Status = [int]$response.StatusCode; Data = ($response.Content | ConvertFrom-Json); Raw = $response.Content }
    }
    catch {
        $webResponse = $_.Exception.Response
        if ($webResponse) {
            $stream = $webResponse.GetResponseStream()
            $reader = New-Object System.IO.StreamReader($stream, [Text.Encoding]::UTF8)
            $text = $reader.ReadToEnd()
            $reader.Dispose()
            try { return @{ Status = [int]$webResponse.StatusCode; Data = ($text | ConvertFrom-Json); Raw = $text } }
            catch { return @{ Status = [int]$webResponse.StatusCode; Data = $null; Raw = $text } }
        }
        return @{ Status = 0; Data = $null; Raw = ("EX: " + $_.Exception.Message) }
    }
}

function Assert-That {
    param([string]$Name, $Condition, [string]$Detail = "")
    if ($Condition) {
        $script:Passed++
        Write-Host ("  [OK]   " + $Name) -ForegroundColor Green
    }
    else {
        $script:Failed++
        $script:Failures.Add($Name + " " + $Detail)
        Write-Host ("  [FAIL] " + $Name + " " + $Detail) -ForegroundColor Red
    }
}

Write-Host "Unity Agent Bridge 冒烟测试  http://127.0.0.1:$Port" -ForegroundColor Cyan
Write-Host "  token 长度=$($Token.Length)" -ForegroundColor DarkGray

# 1. 连通性
$r = Send-Cmd -Cmd "ping"
Assert-That "ping" ($r.Status -eq 200 -and $r.Data -and $r.Data.ok) "status=$($r.Status) body=$($r.Raw)"

# 2. 状态
$r = Send-Cmd -Cmd "status"
Assert-That "status" ($r.Status -eq 200 -and $r.Data.ok -and $r.Data.result.unity) ""

# 3. 命令表
$r = Send-Cmd -Cmd "commands.list"
$commandCount = if ($r.Data.ok) { @($r.Data.result).Count } else { 0 }
Assert-That "commands.list" ($commandCount -ge 30) "count=$commandCount"

# 4. 创建对象
$r = Send-Cmd -Cmd "go.create" -Arguments @{ type = "cube"; name = "__smoke_cube" }
$objectId = $null
if ($r.Data -and $r.Data.ok) { $objectId = $r.Data.result.id }
Assert-That "go.create" ($null -ne $objectId) ""

# 5. 修改变换
$r = Send-Cmd -Cmd "go.set" -Arguments @{ target = "#$objectId"; position = @(5, 1.5, -2) }
Assert-That "go.set" ($r.Data.ok) ""

# 6. 读回变换
$r = Send-Cmd -Cmd "go.get" -Arguments @{ target = "#$objectId" }
$pos = $r.Data.result.transform.position
$posOk = ($pos.Count -eq 3) -and ([math]::Abs($pos[0] - 5) -lt 0.01) -and ([math]::Abs($pos[1] - 1.5) -lt 0.01) -and ([math]::Abs($pos[2] + 2) -lt 0.01)
Assert-That "go.get 位置回读" $posOk "position=$($pos -join ',')"

# 7. 添加组件
$r = Send-Cmd -Cmd "comp.add" -Arguments @{ target = "#$objectId"; type = "Rigidbody" }
Assert-That "comp.add Rigidbody" ($r.Data.ok) ""

# 8. 写组件属性
$r = Send-Cmd -Cmd "comp.set" -Arguments @{ target = "#$objectId"; type = "Rigidbody"; field = "m_Mass"; value = 3.5 }
Assert-That "comp.set m_Mass" ($r.Data.ok) ""

# 9. 读组件属性
$r = Send-Cmd -Cmd "comp.get" -Arguments @{ target = "#$objectId"; type = "Rigidbody" }
$massProp = $null
if ($r.Data.ok) { $massProp = $r.Data.result.properties | Where-Object { $_.path -eq "m_Mass" } | Select-Object -First 1 }
$propPaths = if ($r.Data.ok) { ($r.Data.result.properties | Select-Object -First 6 | ForEach-Object { $_.path }) -join ',' } else { $r.Raw }
$rawSnippet = if ($r.Raw) { $r.Raw.Substring(0, [Math]::Min(300, $r.Raw.Length)) } else { "" }
Assert-That "comp.get m_Mass=3.5" ($massProp -and ([math]::Abs([double]$massProp.value - 3.5) -lt 0.001)) "value=$($massProp.value) paths=$propPaths raw=$rawSnippet"

# 10. 层级树
$r = Send-Cmd -Cmd "hierarchy" -Arguments @{ depth = 1 }
Assert-That "hierarchy" ($r.Data.ok) ""

# 11. 场景信息
$r = Send-Cmd -Cmd "scene.info"
Assert-That "scene.info" ($r.Data.ok) ""

# 12. 日志
$r = Send-Cmd -Cmd "console.get" -Arguments @{ count = 5 }
Assert-That "console.get" ($r.Data.ok) ""

# 13. 资源：建材质
$matPath = "Assets/__BridgeSmoke/Smoke.mat"
$r = Send-Cmd -Cmd "asset.createMaterial" -Arguments @{ path = $matPath; color = "#FF5500" }
Assert-That "asset.createMaterial" ($r.Data.ok) "$($r.Data.error)"

$r = Send-Cmd -Cmd "asset.info" -Arguments @{ path = $matPath }
Assert-That "asset.info" ($r.Data.ok) ""

# 14. batch
$r = Send-Cmd -Cmd "batch" -Arguments @{ calls = @(
        @{ cmd = "ping" },
        @{ cmd = "scene.info" }
    ) }
Assert-That "batch" ($r.Data.ok -and @($r.Data.result.results).Count -eq 2) ""

# 15. 鉴权
$r = Send-Cmd -Cmd "ping" -UseToken "wrong-token"
Assert-That "错误令牌返回 401" ($r.Status -eq 401) "status=$($r.Status)"

# 16. 未知命令
$r = Send-Cmd -Cmd "definitely.not.a.command"
Assert-That "未知命令返回 unknown_command" ($r.Data -and $r.Data.code -eq "unknown_command") ""

# 17. 清理
if ($objectId) {
    $r = Send-Cmd -Cmd "go.delete" -Arguments @{ target = "#$objectId" }
    Assert-That "go.delete" ($r.Data.ok) ""
}
if ($r.Status -eq 200) {
    Send-Cmd -Cmd "asset.delete" -Arguments @{ path = $matPath } | Out-Null
}

# 汇总
Write-Host ""
Write-Host ("通过 " + $script:Passed + " / " + ($script:Passed + $script:Failed)) -ForegroundColor Cyan
if ($script:Failed -gt 0) {
    $script:Failures | ForEach-Object { Write-Host ("  失败: " + $_) -ForegroundColor Red }
    exit 1
}
Write-Host "SMOKE OK" -ForegroundColor Green
exit 0
