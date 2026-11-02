<#
.SYNOPSIS
    用本机已安装的 Unity 创建一个新工程，并自动装上 UnityAgentBridge 插件。

.EXAMPLE
    .\new-unity-project.ps1 -Path "D:\MyGame"

.EXAMPLE
    .\new-unity-project.ps1 -Path "D:\MyGame" -Unity "D:\Program Files\2022.3.62f3c1\Editor\Unity.exe"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Path,

    [string]$Unity = "",
    [switch]$NoPlugin,
    [switch]$NoBatch
)

$ErrorActionPreference = "Stop"

function Find-UnityEditor {
    $candidates = New-Object System.Collections.Generic.List[string]

    if ($env:UNITY_EDITOR) { $candidates.Add($env:UNITY_EDITOR) }

    $hubKey = "HKLM:\SOFTWARE\Unity Technologies\Hub"
    if (Test-Path $hubKey) {
        $hubDir = (Get-ItemProperty $hubKey -ErrorAction SilentlyContinue).InstallLocation
        if ($hubDir) {
            # Unity Hub 的编辑器默认安装在 Hub 所在盘的 Unity\Hub\Editor 或 Program Files\Unity\Hub\Editor
            $roots = @(
                (Join-Path (Split-Path -Qualifier $hubDir) "Program Files\Unity\Hub\Editor"),
                (Join-Path (Split-Path -Qualifier $hubDir) "Unity\Hub\Editor"),
                (Join-Path $env:PROGRAMFILES "Unity\Hub\Editor")
            )
            foreach ($root in $roots) {
                if (Test-Path $root) {
                    Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                        $exe = Join-Path $_.FullName "Editor\Unity.exe"
                        if (Test-Path $exe) { $candidates.Add($exe) }
                    }
                }
            }
        }
    }

    foreach ($root in @("$env:PROGRAMFILES\Unity\Hub\Editor", "$env:PROGRAMFILES\Unity", "D:\Program Files\Unity\Hub\Editor")) {
        if (Test-Path $root) {
            Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                $exe = Join-Path $_.FullName "Editor\Unity.exe"
                if (Test-Path $exe) { $candidates.Add($exe) }
            }
        }
    }

    $existing = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -Unique
    if (-not $existing) { return $null }

    # 选版本号最大的一个
    return $existing | Sort-Object { try { [version](([regex]::Match($_, '\\(\d+\.\d+\.\d+[^\\]*)\\Editor').Groups[1].Value) -replace '[^0-9.]', '') } catch { [version]"0.0" } } -Descending | Select-Object -First 1
}

if (-not $Unity) { $Unity = Find-UnityEditor }
if (-not $Unity -or -not (Test-Path -LiteralPath $Unity)) {
    throw "找不到 Unity.exe，请用 -Unity 指定路径，例如 -Unity 'C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe'"
}

$target = [System.IO.Path]::GetFullPath($Path)
if (Test-Path -LiteralPath (Join-Path $target "Assets")) {
    Write-Host "工程已存在: $target（跳过创建，直接安装插件）" -ForegroundColor Yellow
}
else {
    Write-Host "使用 Unity 创建工程: $target" -ForegroundColor Cyan
    Write-Host "  编辑器: $Unity" -ForegroundColor DarkGray

    $arguments = "-batchmode -nographics -quit -createProject `"$target`" -logFile `"$target.create.log`""
    $process = Start-Process -FilePath $Unity -ArgumentList $arguments -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        throw "创建工程失败（退出码 $($process.ExitCode)），详见 $target.create.log"
    }
    Write-Host "工程已创建" -ForegroundColor Green
}

if (-not $NoPlugin) {
    & (Join-Path $PSScriptRoot "install-plugin.ps1") -ProjectPath $target -Force
}

if (-not $NoBatch) {
    Write-Host "现在可以用 Unity 打开该工程：$target" -ForegroundColor Cyan
}
