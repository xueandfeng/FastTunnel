<#
.SYNOPSIS
    构建 FastTunnel 服务端 / 客户端 Docker 镜像（.NET 10），可选导出 tar 以便离线上传到服务器。

.DESCRIPTION
    镜像版本必须与 Directory.Build.props 里的 <TargetFramework> 一致（当前 net10.0），
    Dockerfile / Dockerfile.Client 已按 10.0 配置。构建前请确认 Docker 引擎（Linux 容器模式）正在运行。

.EXAMPLE
    .\docker-build.ps1
    构建 server + client 两个镜像，tag 取 $Tag（默认 2.3.0）。

.EXAMPLE
    .\docker-build.ps1 -Target server -Tag 2.3.1 -SaveDir D:\images
    只构建服务端镜像并导出 D:\images\fasttunnel-server_2.3.1.tar。

.EXAMPLE
    .\docker-build.ps1 -RegistryPrefix registry.example.com\myteam -Push
    打上前缀并推送。
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'server', 'client')]
    [string] $Target = 'all',

    [string] $Tag = '2.3.0',

    # 镜像名前缀，例如 local（默认，仅本机）或 yourregistry/fasttunnel
    [string] $RegistryPrefix = 'local',

    # 指定后额外执行 docker save 导出 tar，便于 scp 到无外网的服务器
    [string] $SaveDir,

    [switch] $Push
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Invoke-DockerCommand {
    param([string[]] $Arguments)

    Write-Host ("> docker " + ($Arguments -join ' ')) -ForegroundColor DarkGray
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker $($Arguments[0]) 失败，退出码 $LASTEXITCODE"
    }
}

# 先确认引擎可用，避免把错误留给 docker build 的长输出里
try {
    $null = docker version --format '{{.Server.Version}}'
    if ($LASTEXITCODE -ne 0) { throw "docker version 返回 $LASTEXITCODE" }
}
catch {
    throw "Docker 引擎不可用（Windows 请确认 Docker Desktop 已启动并切换到 Linux 容器模式）：$($_.Exception.Message)"
}

$jobs = @()
if ($Target -in @('all', 'server')) {
    $jobs += [pscustomobject]@{
        Name     = 'fasttunnel-server'
        File     = 'Dockerfile'
    }
}
if ($Target -in @('all', 'client')) {
    $jobs += [pscustomobject]@{
        Name     = 'fasttunnel-client'
        File     = 'Dockerfile.Client'
    }
}

$builtImages = @()
foreach ($job in $jobs) {
    $image = "${RegistryPrefix}/$($job.Name):$Tag"
    $latest = "${RegistryPrefix}/$($job.Name):latest"

    Write-Host "`n=== 构建 $image （$($job.File)） ===" -ForegroundColor Cyan
    Invoke-DockerCommand -Arguments @('build', '-f', (Join-Path $root $job.File), '-t', $image, '-t', $latest, $root)
    $builtImages += $image

    if ($Push) {
        Invoke-DockerCommand -Arguments @('push', $image)
    }

    if ($SaveDir) {
        if (-not (Test-Path $SaveDir)) {
            New-Item -ItemType Directory -Path $SaveDir -Force | Out-Null
        }
        $tar = Join-Path $SaveDir ("{0}_{1}.tar" -f $job.Name, $Tag)
        Invoke-DockerCommand -Arguments @('save', '-o', $tar, $image)
        $sizeMb = [math]::Round((Get-Item $tar).Length / 1MB, 1)
        Write-Host "已导出 $tar（$sizeMb MB）" -ForegroundColor Green
    }
}

Write-Host "`n构建完成：" -ForegroundColor Green
$builtImages | ForEach-Object { Write-Host "  $_" }

if ($SaveDir) {
    Write-Host @"

下一步（把 tar 传到服务器后执行）：
  scp fasttunnel-server_$Tag.tar fasttunnel-client_$Tag.tar user@server:/tmp/
  ssh user@server
  ./docker-deploy.sh load /tmp/fasttunnel-server_$Tag.tar
  # 首次部署：手工把 appsettings.Production.json 放到 DATA_DIR/config/（产物与镜像里都不带凭证）
  ./docker-deploy.sh server
"@ -ForegroundColor Yellow
}
else {
    Write-Host "`n提示：本机镜像不会自动出现在服务器上。用 -SaveDir 导出 tar 或加 -Push 推到远端仓库后再部署。" -ForegroundColor Yellow
}
