<#
.SYNOPSIS
    探测 “客户端 => 服务端” 这条 TCP 链路在空闲多久之后会被中间设备（NAT/防火墙/云厂商安全组）
    静默回收，用于验证 FastTunnel “每隔约 1 小时重连一次”是不是链路会话超时造成的。

.DESCRIPTION
    脚本只做一件事：建立一条普通 TCP 长连接，然后按固定节奏观察它什么时候死掉，并区分死法：

      * 读到 0 字节                => 收到对端 FIN（对端主动关闭，状态一般是 CloseWait）
      * 写入抛异常 / 状态消失      => 收到 RST 或被中间设备静默丢弃
      * 一直保持 Established       => 到 -DurationMinutes 结束仍未被回收（说明这段链路不是 1 小时超时）

    关键区别：如果整个过程中本端一个字节都不发，静默丢弃是探不出来的（没有包就没有失败）。
    所以要么打开 -KeepAliveSeconds 让内核周期发 TCP keep-alive（默认方式，最安全），
    要么用 -SendEverySeconds 周期写探测字节。

    目标端口的选择很重要：往严格的服务（如 sshd）写垃圾字节会被立即断开，那测不出空闲超时。
    推荐顺序：
      a) 自己控制的 tolerant 端口（如在另一台内网机上 nc -lkp 9999，或一个不校验的 HTTP 端口）
      b) 纯 keep-alive 模式（-SendEverySeconds 0，默认）
      c) 直接打到 FastTunnel 的 1270：非协议字节会让服务端记一条“处理客户端消息失败”，
         属于预期内噪声，但服务端可能会主动踢掉这条连接

    建议在两个位置各跑一次，用来切分故障域：
      1) 在客户端所在网络（尤其是那个 Docker 宿主机的同一出口）跑 —— 复现现场；
      2) 在一台同机房/同云的机器上直连服务端跑 —— 作为对照组。

    Linux（客户端容器所在环境）等价命令：
      timeout 9000 bash -c 'exec 3<>/dev/tcp/1.2.3.4/1270; while :; do sleep 20; echo -n "" >&3 || break; done'
      ss -tnpo state established 'dst 1.2.3.4'   # 看 keepalive 定时器与实际存活时间

.EXAMPLE
    # 默认：纯空闲 + 内核 keep-alive（30s），观察最长 150 分钟（覆盖 1 小时超时点）
    .\tcp_session_probe.ps1 -Server 1.2.3.4 -Port 1270 -DurationMinutes 150

.EXAMPLE
    # 主动写探测字节（目标端口必须能容忍垃圾字节）
    .\tcp_session_probe.ps1 -Server 1.2.3.4 -Port 9999 -DurationMinutes 150 -SendEverySeconds 60 -KeepAliveSeconds 0

.EXAMPLE
    # 对照组：本机起一个监听，验证脚本本身没问题
    .\tcp_session_probe.ps1 -Server 127.0.0.1 -Port 9999 -DurationMinutes 5
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Server,
    [Parameter(Mandatory = $true)][int] $Port,

    # 观察总时长（分钟）。要验证 1 小时超时，建议 >= 150
    [int] $DurationMinutes = 150,

    # 轮询/记日志的间隔（秒）
    [int] $IntervalSeconds = 20,

    # 每多少秒写 1 个探测字节；0 = 不写（纯空闲，此时只能靠 keep-alive 或读来判断）
    [int] $SendEverySeconds = 0,

    # 启用 TCP keep-alive 并设置探测间隔（秒）；0 = 不启用
    [int] $KeepAliveSeconds = 30,

    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'

function Write-Log {
    param([string] $Message, [string] $Level = 'INFO')

    if ($Quiet -and $Level -eq 'INFO') { return }

    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $color = switch ($Level) {
        'WARN' { 'Yellow' }
        'FAIL' { 'Red' }
        'OK'   { 'Green' }
        default { 'Gray' }
    }
    Write-Host "[$ts] [$Level] $Message" -ForegroundColor $color
}

function Get-LocalTcpState {
    param([int] $LocalPort)

    try {
        $conn = Get-NetTCPConnection -LocalPort $LocalPort -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($conn) { return $conn.State.ToString() }
    }
    catch {
        # 某些环境没有 Get-NetTCPConnection（精简系统/非 Windows），忽略
    }

    return 'unknown'
}

$endpoint = $null
try {
    $ip = [System.Net.IPAddress]::Parse($Server)
    $endpoint = New-Object System.Net.IPEndPoint($ip, $Port)
}
catch {
    # 不是 IP 就走 DNS 解析
    $entry = [System.Net.Dns]::GetHostAddresses($Server) | Select-Object -First 1
    if (-not $entry) { throw "无法解析 $Server" }
    $endpoint = New-Object System.Net.IPEndPoint($entry, $Port)
}

Write-Log "目标 $($endpoint.Address):$Port，观察 $($DurationMinutes) 分钟，轮询间隔 $($IntervalSeconds)s，写探测间隔 $(if ($SendEverySeconds -gt 0) { "$($SendEverySeconds)s" } else { '关闭' })，keep-alive $(if ($KeepAliveSeconds -gt 0) { "$($KeepAliveSeconds)s" } else { '关闭' })"

$socket = New-Object System.Net.Sockets.Socket([System.Net.Sockets.AddressFamily]::InterNetwork,
    [System.Net.Sockets.SocketType]::Stream, [System.Net.Sockets.ProtocolType]::Tcp)
$socket.SendTimeout = 5000
$socket.ReceiveTimeout = 5000

if ($KeepAliveSeconds -gt 0) {
    # 打开内核 keep-alive；具体间隔参数在 Windows PowerShell 5.1（.NET Framework）下可能不支持，
    # 失败就只保留默认间隔（Windows 默认 10s 开始、每秒一次；Linux 默认 7200s），脚本会给出提示。
    try {
        $socket.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::Tcp,
            [System.Net.Sockets.SocketOptionName]::KeepAlive, [byte[]](1, 0, 0, 0))
        Write-Log "已启用内核 keep-alive（期望间隔 $KeepAliveSeconds 秒）"
    }
    catch {
        Write-Log "启用 keep-alive 失败：$($_.Exception.Message)" 'WARN'
    }

    # 21 = TCP_KEEPIDLE，22 = TCP_KEEPINTVL（.NET Core / PowerShell 7 才认名字，这里用数值）
    foreach ($name in 21, 22) {
        try {
            $socket.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::Tcp,
                [System.Net.Sockets.SocketOptionName]$name, $KeepAliveSeconds)
        }
        catch {
            Write-Log "设置 SocketOption $name 不被支持，使用系统默认 keep-alive 间隔" 'WARN'
            break
        }
    }
}

$connectStarted = Get-Date
try {
    $ar = $socket.BeginConnect($endpoint, $null, $null)
    if (-not $ar.AsyncWaitHandle.WaitOne(10000, $false)) { throw "连接超时(10s)" }
    $socket.EndConnect($ar)
}
catch {
    Write-Log "连接失败：$($_.Exception.Message)" 'FAIL'
    Write-Log '结论：连不上就没有下文了，请先确认端口可达/安全组放行' 'FAIL'
    $socket.Dispose()
    return
}

$localPort = $socket.LocalEndPoint.Port
Write-Log "已连接（本地端口 $localPort，状态 $(Get-LocalTcpState $localPort)）" 'OK'

$buffer = [byte[]]::new(4096)
$probe = [byte[]]::new(1)     # 1 个 0x00 字节，只为验证链路是否还能送出包
$started = Get-Date
$lastSend = Get-Date
$sendCount = 0
$result = 'ALIVE'

try {
    while ($true) {
        $elapsed = (Get-Date) - $started
        if ($elapsed.TotalMinutes -ge $DurationMinutes) {
            Write-Log "达到观察时长 $($DurationMinutes) 分钟，链路仍然存活（共写探测 $sendCount 次）" 'OK'
            break
        }

        Start-Sleep -Seconds $IntervalSeconds
        $elapsed = (Get-Date) - $started
        $state = Get-LocalTcpState $localPort

        # 1) 可读但读到 0 字节 => 对端发了 FIN
        if ($socket.Poll(0, [System.Net.Sockets.SelectMode]::SelectRead)) {
            try {
                $read = $socket.Receive($buffer)
                if ($read -eq 0) {
                    Write-Log "连接被对端关闭（收到 FIN，本地状态 $state），本次空闲存活 $([int]$elapsed.TotalSeconds) 秒" 'FAIL'
                    $result = 'CLOSED-BY-PEER'
                    break
                }

                Write-Log "读到 $read 字节（对端有数据返回，说明链路正常）"
            }
            catch {
                Write-Log "读取失败：$($_.Exception.Message)（本地状态 $state），本次空闲存活 $([int]$elapsed.TotalSeconds) 秒" 'FAIL'
                $result = 'READ-ERROR'
                break
            }
        }

        # 2) 周期写探测字节：静默丢弃只有在下一次写入时才会暴露
        if ($SendEverySeconds -gt 0 -and ((Get-Date) - $lastSend).TotalSeconds -ge $SendEverySeconds) {
            try {
                $null = $socket.Send($probe)
                $sendCount++
                $lastSend = Get-Date
                Write-Log "存活 $([int]$elapsed.TotalSeconds)s，写探测 #$sendCount 成功，状态 $state"
            }
            catch {
                Write-Log "写探测失败：$($_.Exception.Message)（本地状态 $state），说明链路在 $([int]$elapsed.TotalSeconds) 秒内已被回收" 'FAIL'
                $result = 'SEND-ERROR'
                break
            }
        }
        elseif ($SendEverySeconds -le 0) {
            Write-Log "存活 $([int]$elapsed.TotalSeconds)s，状态 $state（纯空闲观察）"
        }
    }
}
finally {
    try { $socket.Shutdown([System.Net.Sockets.ShutdownType]::Both) } catch { }
    $socket.Dispose()

    $totalSeconds = [int](((Get-Date) - $started).TotalSeconds)
    Write-Log "结束：结果=$result，总时长 ${totalSeconds}s，写探测 $sendCount 次，最终本地状态 $(Get-LocalTcpState $localPort)"

    if ($result -eq 'ALIVE') {
        Write-Log '判读：这条链路在本次观察期内没有被回收。如果 FastTunnel 仍在整点重连，重点转向容器/进程被重启或应用层配置。' 'OK'
    }
    else {
        Write-Log '判读：链路确实会被回收。把客户端 HeartbeatSeconds/WsKeepAliveSeconds 调到小于该存活时长即可规避（默认 30s/15s）。' 'WARN'
    }
}
