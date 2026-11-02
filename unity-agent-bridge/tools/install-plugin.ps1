<#
.SYNOPSIS
    把 UnityAgentBridge 插件安装到指定的 Unity 工程。

.EXAMPLE
    .\install-plugin.ps1 -ProjectPath "D:\MyGame"

.EXAMPLE
    .\install-plugin.ps1 -ProjectPath "D:\MyGame" -AsPackage
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$ProjectPath,

    # 作为本地 UPM 包安装到 Packages/ 而不是 Assets/
    [switch]$AsPackage,

    [switch]$Force
)

$ErrorActionPreference = "Stop"

$pluginSource = Join-Path (Split-Path -Parent $PSScriptRoot) "UnityAgentBridge"
if (-not (Test-Path -LiteralPath $pluginSource)) {
    throw "找不到插件源目录: $pluginSource"
}

$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$versionFile = Join-Path $project "ProjectSettings\ProjectVersion.txt"
if (-not (Test-Path -LiteralPath $versionFile)) {
    throw "$project 不像一个 Unity 工程（缺少 ProjectSettings\ProjectVersion.txt）"
}

$unityVersion = (Get-Content -LiteralPath $versionFile | Select-String 'm_EditorVersion:').Line -replace 'm_EditorVersion:\s*', ''

if ($AsPackage) {
    $target = Join-Path $project "Packages\com.dsh.unity-agent-bridge"
}
else {
    $target = Join-Path $project "Assets\UnityAgentBridge"
}

if (Test-Path -LiteralPath $target) {
    if (-not $Force) {
        Write-Host "目标已存在: $target（加 -Force 覆盖）" -ForegroundColor Yellow
        exit 1
    }
    Remove-Item -LiteralPath $target -Recurse -Force
}

$parent = Split-Path -Parent $target
if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

Copy-Item -LiteralPath $pluginSource -Destination $target -Recurse

Write-Host "已安装到: $target" -ForegroundColor Green
Write-Host "Unity 版本: $unityVersion" -ForegroundColor DarkGray
Write-Host ""
Write-Host "下一步：" -ForegroundColor Cyan
Write-Host "  1. 用 Unity 打开该工程，等待编译完成（Console 不应有报错）"
Write-Host "  2. 菜单 Tools -> Unity Agent Bridge 打开控制面板，确认服务已启动"
Write-Host "  3. 用客户端连接:  .\client\unityctl.ps1 ping -Project '$project'"
