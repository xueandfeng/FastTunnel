// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Config;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers.Client;
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using FastTunnel.Core.Utilitys;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FastTunnel.Core.Client;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(LogInMassage))]
[JsonSerializable(typeof(Message<TunnelMassage>))]
public partial class SourceGenerationContext : JsonSerializerContext
{
}

public class FastTunnelClient : IFastTunnelClient
{
    private ClientWebSocket? socket;

    /// <summary>
    /// 当前会话的读循环，保留引用以便在连接结束后读取 <see cref="WebSocketUtility.EndReason"/>，
    /// 用于定位“不定时重连”到底是链路、对端还是本端先断的。
    /// </summary>
    private WebSocketUtility? utility;

    private Timer? heartbeatTimer;

    /// <summary>心跳单飞标记，避免上一次发送未完成时堆积。</summary>
    private int heartbeatPending;

    /// <summary>
    /// 会话主动轮换定时器（仅 MaxSessionMinutes &gt; 0 时创建）。每个会话一个，随会话结束 Dispose；
    /// 只对它自己那条连接生效（见 RotateSessionAsync 里的归属判定）。
    /// </summary>
    private Timer? rotationTimer;

    /// <summary>
    /// 本轮会话的实际轮换时刻（秒，0 = 不开）。它是 MaxSessionMinutes 加上本轮随机抖动后的结果，
    /// 日志只能报这个值，报配置阈值会让人拿它去对时间线而对不上。
    /// </summary>
    private int rotationDueSeconds;

    /// <summary>
    /// 本轮会话是不是自己主动轮换结束的（0 = 不是）。它决定日志口径与要不要退避：
    /// 把计划内轮换记成一次异常掉线，既会把告警带偏，也会把后续真实故障的退避起点抬高一档。
    /// </summary>
    private int rotationRequested;

    /// <summary>0 = 尚未确认，1 = 服务端支持应用层心跳，-1 = 服务端不支持（已降级）。</summary>
    private int heartbeatState;

    private DateTime lastRecvUtc = DateTime.UtcNow;
    private DateTime firstHeartbeatUtc;
    private int consecutiveFailures;

    /// <summary>上一轮退避是否是被“端口探活通了”提前打断的，用于识别探活不可信的场景。</summary>
    private bool lastBackoffCutShort;

    /// <summary>“端口探活通了但 WebSocket 仍没建立”的连续次数，达到上限就不再靠探活加速。</summary>
    private int probeMisleadingStreak;

    /// <summary>true = 探活结果不可信（多半是中间有反代/负载均衡顶着端口），已退回完整退避。</summary>
    private bool probeFallback;

    /// <summary>单次端口探测的超时（秒）。链路真断时 SYN 可能无人应答，必须限时。</summary>
    private const int ProbeTimeoutSeconds = 2;

    /// <summary>连续这么多次“端口通了但连不上”就判定探活失真，停止加速。</summary>
    private const int MaxProbeMisleadingStreak = 3;

    // 下面几个计数只用于诊断，帮助区分“全程空连接被设备回收”还是“业务繁忙时被挤掉”。
    private long recvFrames;
    private long sentFrames;
    private long swapFrames;
    private long heartbeatTicks;
    private long lastRttMs = -1;

    protected readonly ILogger<FastTunnelClient> _logger;
    protected DefaultClientConfig ClientConfig { get; private set; }

    private readonly SwapHandler swapHandler;
    private readonly LogHandler logHandler;

    public SuiDaoServer Server { get; protected set; }

    public FastTunnelClient(
        ILogger<FastTunnelClient> logger,
        SwapHandler newCustomerHandler,
        LogHandler logHandler,
        IOptionsMonitor<DefaultClientConfig> configuration)
    {
        _logger = logger;
        swapHandler = newCustomerHandler;
        this.logHandler = logHandler;
        ClientConfig = configuration.CurrentValue;
        Server = ClientConfig.Server;
    }

    /// <summary>
    /// 启动客户端
    /// </summary>
    public virtual async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("===== FastTunnel Client Start =====");

        while (!cancellationToken.IsCancellationRequested)
        {
            var startUtc = DateTime.UtcNow;
            Exception? fault = null;
            utility = null;
            recvFrames = 0;
            sentFrames = 0;
            swapFrames = 0;
            heartbeatTicks = 0;
            lastRttMs = -1;
            Interlocked.Exchange(ref heartbeatPending, 0);
            Interlocked.Exchange(ref rotationRequested, 0);
            // 轮换时刻只给下面算日志用，不在 StopRotation 里清（那会发生在读循环退出之后、记日志之前），
            // 改在每轮开头清，免得把上一轮的时刻当成这一轮的。
            Volatile.Write(ref rotationDueSeconds, 0);

            try
            {
                await loginAsync(cancellationToken);
                StartHeartbeat();
                StartRotation();
                await ReceiveServerAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 进程正在停止，不算异常
            }
            catch (Exception ex)
            {
                fault = ex;
            }
            finally
            {
                StopHeartbeat();
                // 轮换定时器必须随会话一起停。上一轮如果在第 3 分钟就掉了线，定时器不拆就会
                // 在第 N 分钟把当时活着的新会话一刀切断 —— 看起来就像“不定时重连”自己多了起来。
                StopRotation();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var alive = DateTime.UtcNow - startUtc;

            // 探活加速的防呆：本轮至少把 WebSocket 握上了（utility 非空），说明对端确实是“挂了又回来”，
            // 探活结果是可信的，把上一轮留下的计数清零；反之“端口明明通了却还是连不上”
            // 累计到上限，就判定中间有东西顶着端口（反代 / 负载均衡 / 证书或 Token 被拒），
            // 再加速只会变成几秒一次的死循环，退回完整退避。
            if (utility != null)
            {
                lastBackoffCutShort = false;
                probeMisleadingStreak = 0;
                probeFallback = false;
            }
            else if (lastBackoffCutShort && ++probeMisleadingStreak >= MaxProbeMisleadingStreak)
            {
                probeFallback = true;
                _logger.LogWarning($"[Control] 连续 {probeMisleadingStreak} 次探到服务端端口开放但 WebSocket 仍未建立，"
                    + "暂停端口探活加速，恢复完整退避（常见原因：中间有反代/负载均衡顶着端口，或握手被服务端拒绝）");
            }

            // 能撑过 1 分钟的会话说明不是本地反复闪断，多半是链路中间设备把会话回收了，
            // 重连间隔退回基础值；只有在短时间内连续掉线才做指数退避。
            // 主动轮换是计划内动作，既不算快速掉线也不算链路故障，单独走 INFO + 立即重连。
            var rotated = Volatile.Read(ref rotationRequested) == 1 && !cancellationToken.IsCancellationRequested;
            consecutiveFailures = (rotated || alive.TotalSeconds >= 60) ? 0 : consecutiveFailures + 1;
            var delay = NextReconnectDelaySeconds(consecutiveFailures);
            var reason = DescribeEnd(fault);
            var silent = (int)(DateTime.UtcNow - lastRecvUtc).TotalSeconds;
            var stats = $"收帧 {recvFrames}（Swap {swapFrames}）｜发帧 {sentFrames}（心跳 {heartbeatTicks}）｜RTT {RttText()}｜距上次入站 {silent}s 前";
            var probeHint = ShouldProbeDuringBackoff() ? $"｜期间每 {ClientConfig.ReconnectProbeIntervalSeconds}s 探活端口，通了立即重连" : "";

            if (rotated)
            {
                _logger.LogInformation($"[Control] 会话按轮换计划主动结束（本次到期时刻 {FormatDue(Volatile.Read(ref rotationDueSeconds))}"
                    + $"，配置阈值 {ClientConfig.MaxSessionMinutes} 分钟）｜本次在线 {FormatAlive(alive)}｜{stats}｜立即重连（不等退避）");
            }
            else if (alive.TotalSeconds >= 60)
            {
                _logger.LogWarning($"[Control] 会话结束：{reason}｜本次在线 {FormatAlive(alive)}｜{stats}｜{delay}s 后重连{probeHint}");
            }
            else
            {
                _logger.LogError($"[Control] 会话结束：{reason}｜本次在线 {FormatAlive(alive)}｜{stats}｜连续快速掉线 {consecutiveFailures + 1} 次｜{delay}s 后重连{probeHint}");
            }

            if (fault != null)
            {
                _logger.LogDebug(fault, "[Control] 会话异常详情");
            }

            try
            {
                if (rotated)
                {
                    // 主动轮换后不等退避：刚刚还是健康的连接，服务端就在那里，多等 5 秒只是
                    // 多给本实例的后端注册挖 5 秒坑（靠另一台顶上，但坑越浅越好）。
                    // 若紧接着的重连真的失败，下一轮会照常进入退避，不会在这转空循环。
                    lastBackoffCutShort = false;
                }
                else if (ShouldProbeDuringBackoff())
                {
                    lastBackoffCutShort = await WaitForServerOpenAsync(delay, cancellationToken);
                    if (lastBackoffCutShort)
                    {
                        _logger.LogInformation($"[Control] 探到服务端 {Server.ServerAddr}:{Server.ServerPort} 端口已开放，提前结束退避（本轮原需等 {delay}s）");
                    }
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                    lastBackoffCutShort = false;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("===== FastTunnel Client End =====");
    }

    /// <summary>
    /// 汇总本次连接结束的原因：优先使用读循环记录的退出原因，其次使用抛出的异常。
    /// </summary>
    private string DescribeEnd(Exception? fault)
    {
        var endReason = utility?.EndReason ?? "未完成连接（未进入读循环）";

        if (fault != null && (utility == null || utility.EndException == null))
        {
            var detail = $"{fault.GetType().Name}: {fault.Message}";
            endReason = endReason == "running" || endReason.StartsWith("收到 ")
                ? detail
                : $"{endReason}（{detail}）";
        }

        return endReason;
    }

    private static string FormatAlive(TimeSpan alive)
    {
        return alive.TotalHours >= 1
            ? $"{(int)alive.TotalHours}小时{alive.Minutes:00}分{alive.Seconds:00}秒"
            : $"{alive.Minutes:00}分{alive.Seconds:00}秒";
    }

    private string RttText()
    {
        return lastRttMs < 0 ? "未测到" : $"{lastRttMs}ms";
    }

    /// <summary>
    /// 指数退避：ReconnectDelaySeconds * 2^failures，上限 MaxReconnectDelaySeconds。
    /// </summary>
    private int NextReconnectDelaySeconds(int failures)
    {
        var baseDelay = ClientConfig.ReconnectDelaySeconds > 0 ? ClientConfig.ReconnectDelaySeconds : 1;
        var cap = Math.Max(baseDelay, ClientConfig.MaxReconnectDelaySeconds);

        var delay = (double)baseDelay;
        for (var i = 0; i < failures && delay < cap; i++)
        {
            delay *= 2;
        }

        return (int)Math.Min(delay, cap);
    }

    /// <summary>
    /// 本轮退避是否适合附带端口探活：只针对“整轮都没把 WebSocket 握上”的失败。
    /// 已经握过手又掉线的走原退避 —— 那种情况下端口本来就是开的，探活一测就通，
    /// 等于把退避直接抹掉，只会在服务端反复拒接时把日志刷爆。
    /// </summary>
    private bool ShouldProbeDuringBackoff()
    {
        return !probeFallback
            && ClientConfig.ReconnectProbeIntervalSeconds > 0
            && utility == null;
    }

    /// <summary>
    /// 退避等待期间盯住服务端端口：每隔 ReconnectProbeIntervalSeconds 做一次 TCP 连接探测，
    /// 端口一开就提前结束等待，把“服务端恢复”到“本端重连”的延迟从退避上限（默认 60s）压到几秒。
    /// 探测代价很低（一个 SYN，对端没监听当场 RST）；探不到就照原样等满，总等待不会比不探活时更长。
    /// </summary>
    /// <returns>true = 因探到端口开放而提前结束；false = 等满了。</returns>
    private async Task<bool> WaitForServerOpenAsync(int maxWaitSeconds, CancellationToken cancellationToken)
    {
        var interval = Math.Max(1, ClientConfig.ReconnectProbeIntervalSeconds);
        var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);

        while (true)
        {
            var remain = (deadline - DateTime.UtcNow).TotalSeconds;
            if (remain <= 0)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Min(interval, remain)), cancellationToken);

            if (await ProbeServerPortAsync(cancellationToken))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// 一次轻量端口探测：只建 TCP 连接不发任何业务报文，连通就当场丢弃。
    /// 被拒 / 不可达 / 超时都返回 false，不停机流程则由 cancellationToken 抛出退出。
    /// </summary>
    private async Task<bool> ProbeServerPortAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(ProbeTimeoutSeconds));

            using var probe = new TcpClient();
            await probe.ConnectAsync(Server.ServerAddr, Server.ServerPort, timeoutCts.Token);
            return true;
        }
        catch (Exception)
        {
            // 包含“被拒（端口未监听）”、“网络不可达”、“探测超时”，以及停机取消；
            // 停机时下一轮的 cancellationToken 判定会把循环收尾，这里不用特殊处理
            return false;
        }
    }

    private async Task loginAsync(CancellationToken cancellationToken)
    {
        var logMsg = GetLoginMsg(cancellationToken);

        if (socket != null)
        {
            try
            {
                socket.Abort();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Control] 清理旧 socket 失败");
            }
        }

        // 连接到的目标IP
        var client = new ClientWebSocket();
        client.Options.RemoteCertificateValidationCallback = delegate { return true; };
        client.Options.SetRequestHeader(FastTunnelConst.FASTTUNNEL_VERSION, AssemblyUtility.GetVersion().ToString());
        client.Options.SetRequestHeader(FastTunnelConst.FASTTUNNEL_TOKEN, ClientConfig.Token);

        if (ClientConfig.WsKeepAliveSeconds > 0)
        {
            // 更密的 WebSocket ping 控制帧，既能探出半开链路，也能刷新 NAT/防火墙会话
            client.Options.KeepAliveInterval = TimeSpan.FromSeconds(ClientConfig.WsKeepAliveSeconds);
        }

        socket = client;
        var url = $"{Server.Protocol}://{Server.ServerAddr}:{Server.ServerPort}";

        if (ClientConfig.PingBeforeConnect)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(Server.ServerAddr, 1000);
                _logger.LogDebug($"Ping {Server.ServerAddr} status={reply.Status}, roundTrip={reply.RoundtripTime}ms");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"Ping {Server.ServerAddr} 失败");
            }
        }

        _logger.LogInformation($"正在连接服务端 {url}");

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            // 链路被中间设备静默丢弃时，TCP 握手可能挂很久，这里限时失败以尽快进入下一次重连
            connectCts.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await client.ConnectAsync(new Uri(url), connectCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"连接服务端超时(20s)：{url}");
            }
        }

        _logger.LogInformation($"控制通道已连上 {url}｜WS 控制帧间隔 {(ClientConfig.WsKeepAliveSeconds > 0 ? ClientConfig.WsKeepAliveSeconds + "s" : "默认 30s")}｜"
            + $"应用层心跳 {(ClientConfig.HeartbeatSeconds > 0 ? ClientConfig.HeartbeatSeconds + "s" : "已关闭")}｜"
            + $"会话轮换 {(ClientConfig.MaxSessionMinutes > 0 ? $"已开（阈值 {ClientConfig.MaxSessionMinutes} 分钟 + 0~{ClientConfig.SessionRotateJitterSeconds} 秒抖动，具体时刻见下一条日志）" : "已关闭")}｜本地版本 {AssemblyUtility.GetVersion()}");
        Interlocked.Increment(ref sentFrames);

        // 登录
        using (var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            sendCts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await client.SendCmdAsync(MessageType.LogIn, logMsg, sendCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"发送登录指令超时(15s)：{url}");
            }
        }
    }


    protected virtual string GetLoginMsg(CancellationToken cancellationToken)
    {
        Server = ClientConfig.Server;

        return new LogInMassage
        {
            Webs = ClientConfig.Webs,
            Forwards = ClientConfig.Forwards,
        }.ToJson(jsonTypeInfo: SourceGenerationContext.Default.LogInMassage);
    }


    protected async Task ReceiveServerAsync(CancellationToken cancellationToken)
    {
        var current = socket ?? throw new InvalidOperationException("socket 未初始化");
        var currentUtility = new WebSocketUtility(current, ProcessLine);
        utility = currentUtility;
        lastRecvUtc = DateTime.UtcNow;
        await currentUtility.ProcessLinesAsync(cancellationToken);
    }

    private void ProcessLine(ReadOnlySequence<byte> line, CancellationToken cancellationToken)
    {
        HandleServerRequestAsync(line, cancellationToken);
    }

    private void HandleServerRequestAsync(ReadOnlySequence<byte> line, CancellationToken cancellationToken)
    {
        try
        {
            if (line.Length == 0)
            {
                // 空行（心跳之外的 keepalive 或对端异常）不影响读循环，刷新活跃时间后忽略
                lastRecvUtc = DateTime.UtcNow;
                return;
            }

            var row = line.ToArray();
            var cmd = row[0];

            var content = Encoding.UTF8.GetString(line.Slice(1));
            // 收到任何入站数据都说明链路是活的
            lastRecvUtc = DateTime.UtcNow;
            recvFrames++;

            IClientHandler handler;
            switch ((MessageType)cmd)
            {
                case MessageType.HeartBeat:
                    HandleHeartbeatReply(content);
                    return;
                case MessageType.SwapMsg:
                case MessageType.Forward:
                    swapFrames++;
                    handler = swapHandler;
                    break;
                case MessageType.Log:
                    handler = logHandler;
                    break;
                default:
                    throw new Exception($"未处理的消息：cmd={cmd}");
            }

            handler.HandlerMsgAsync(this, content, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex);
        }
    }

    /// <summary>
    /// 启动应用层心跳：刷新中间设备会话 + 探出半开链路 + 记录 RTT。
    /// 负载是“hb {ticks}”纯文本而非 JSON，老服务端即使不认也不会误当成登录重放。
    /// </summary>
    private void StartHeartbeat()
    {
        StopHeartbeat();

        lastRecvUtc = DateTime.UtcNow;
        firstHeartbeatUtc = default;

        var seconds = ClientConfig.HeartbeatSeconds;
        if (seconds <= 0)
        {
            return;
        }

        if (heartbeatState < 0)
        {
            _logger.LogDebug("[Control] 已确认服务端不支持应用层心跳，本次连接仅依赖 WebSocket 控制帧保活");
            return;
        }

        heartbeatTimer = new Timer(
            state => _ = SendHeartbeatAsync(),
            null,
            TimeSpan.FromSeconds(seconds),
            TimeSpan.FromSeconds(seconds));
    }

    private void StopHeartbeat()
    {
        Interlocked.Exchange(ref heartbeatTimer, null)?.Dispose();
    }

    /// <summary>
    /// 启动会话主动轮换：到点就自己把这条连接换掉。
    /// 它治的是“最长会话时长”那一类硬上限：中间设备（NAT/防火墙/上网行为管理）常见 7200 秒
    /// 封顶，那种上限不看流量、也不给拆链报文，心跳发得再密也会被砍（实测本链路两条连接
    /// 都在在线整 2 小时时被打断，而当时 RTT 5-8ms、心跳回包正常）。唯一能控的手段就是自己先到点重连，
    /// 把断开从“设备选的时刻”改成“本端选的时刻”。
    /// 动作与优雅停机同一套：发 WebSocket Close 帧。服务端的读循环会当场读到 Close 并注销会话、
    /// 摘掉本实例的后端注册（而不是等一条悬空的死后端），同时本端的读循环也因收到对端 Close 帧自然退出。
    /// MaxSessionMinutes &lt;= 0 时不开（默认），单实例部署可以保持关闭。
    /// 实际到期时刻还要加上 SessionRotateJitterSeconds 以内的随机秒数，让多台客户端不必人工错开取值
    /// 也不会撞在同一刻一起换连接。
    /// </summary>
    private void StartRotation()
    {
        StopRotation();

        var minutes = ClientConfig.MaxSessionMinutes;
        if (minutes <= 0)
        {
            return;
        }

        // 把本条连接固定给定时器：Dispose 与回调存在竞态窗口，不判归属就会误伤下一轮会话。
        var target = socket;
        if (target == null)
        {
            return;
        }

        // 按秒取随机而不是按分钟：两台就算配了同样的阈值，也要落在同一秒才算重合，
        // 撞车概率从“同一分钟”量级降到秒级。每轮重新取，所以上一轮撞过不代表这一轮还撞。
        var jitter = ClientConfig.SessionRotateJitterSeconds > 0 ? ClientConfig.SessionRotateJitterSeconds : 0;
        var dueSeconds = (minutes * 60) + (jitter > 0 ? Random.Shared.Next(0, jitter + 1) : 0);
        Volatile.Write(ref rotationDueSeconds, dueSeconds);

        rotationTimer = new Timer(
            _ => _ = RotateSessionAsync(target),
            null,
            TimeSpan.FromSeconds(dueSeconds),
            Timeout.InfiniteTimeSpan);

        _logger.LogInformation($"[Control] 会话轮换已排期：本次在线满 {FormatDue(dueSeconds)} 后主动重连（提前躲开中间设备的会话时长上限）"
            + $"｜阈值 {minutes} 分钟｜抖动 0~{jitter} 秒");
    }

    private void StopRotation()
    {
        Interlocked.Exchange(ref rotationTimer, null)?.Dispose();
    }

    /// <summary>
    /// 把轮换到期秒数说成人能读的形式（不带毫秒，也不要“0小时”这种噪声）。
    /// </summary>
    private static string FormatDue(int dueSeconds)
    {
        var span = TimeSpan.FromSeconds(dueSeconds);
        return span.Hours > 0
            ? $"{(int)span.TotalHours}小时{span.Minutes:00}分{span.Seconds:00}秒"
            : $"{span.Minutes}分{span.Seconds:00}秒";
    }

    /// <summary>
    /// 到点把本条连接主动换掉。只在“它还是当前活动的那条、且仍处于 Open”时执行；
    /// 否则就当这次轮换没发生（日志与退避口径仍归为一次真实掉线）。
    /// </summary>
    private async Task RotateSessionAsync(ClientWebSocket target)
    {
        if (!ReferenceEquals(target, socket) || target.State != WebSocketState.Open)
        {
            return;
        }

        // 先置位再发帧：Close 帧一出去读循环就会退出，置位晚一步就会被当成异常掉线。
        Interlocked.Exchange(ref rotationRequested, 1);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await target.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "session rotate", cts.Token);
            _logger.LogDebug("[Control] 已发出轮换用 Close 帧，服务端会当场注销本会话");
        }
        catch (Exception ex)
        {
            // 链路正好在这一步断了，Close 帧发不出去：Abort 收场，读循环会当场退出。
            // 口径仍按“计划内轮换”计（不给告警与退避添噪），服务端那边靠拆链报文或空闲超时兜底。
            _logger.LogDebug(ex, "[Control] 轮换发送 Close 帧失败，直接 Abort 本条连接");
            AbortQuietly(target);
            return;
        }

        // CloseOutputAsync 只管把本端的 Close 帧发出去，不等对端回帧（要等就可能一直卡在这一步）。
        // 但对端就是不回时，本端读循环不会退出，这次轮换等于没发生，最后还是被设备在会话上限那一刀砍掉 ——
        // 那正是本功能要躲的东西。所以给 5 秒：状态没走到 Closed 就 Abort 收尾，读循环当场退出。
        for (var i = 0; i < 10 && !IsClosed(target); i++)
        {
            await Task.Delay(500);
        }

        if (!IsClosed(target))
        {
            _logger.LogWarning("[Control] 轮换的 Close 帧已发出但 5 秒内没等到对端回帧，改 Abort 收尾（不让读循环一直挂在这一步）"
                + "｜服务端那边靠拆链报文或 ClientIdleTimeoutSeconds 注销本会话");
            AbortQuietly(target);
        }
    }

    /// <summary>
    /// 轮换收尾用的 Abort：失败也不另写一条日志（调用方已经把本轮的退出原因报了）。
    /// </summary>
    private void AbortQuietly(ClientWebSocket target)
    {
        try
        {
            target.Abort();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Control] 轮换 Abort 失败");
        }
    }

    /// <summary>
    /// 关闭握手是否已经走完（或本端已不需要再等）。已 Dispose 的 WebSocket 读 State 会抛异常，
    /// 这里一律当作“已关”：不能让一个收尾判断把轮换流程带倒。
    /// CloseSent 不算已关（它恰好是“我发了 Close、还在等对端回帧”那个状态），
    /// CloseReceived 则说明对端先说了再见，读循环本来就会退出。
    /// </summary>
    private static bool IsClosed(ClientWebSocket target)
    {
        try
        {
            return target.State == WebSocketState.Closed
                || target.State == WebSocketState.Aborted
                || target.State == WebSocketState.CloseReceived;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private async Task SendHeartbeatAsync()
    {
        if (Interlocked.CompareExchange(ref heartbeatPending, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var current = socket;
            if (current == null || current.State != WebSocketState.Open)
            {
                return;
            }

            if (firstHeartbeatUtc == default)
            {
                firstHeartbeatUtc = DateTime.UtcNow;
            }

            var interval = ClientConfig.HeartbeatSeconds;

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    await current.SendCmdAsync(
                        MessageType.HeartBeat,
                        $"{Heartbeat.PayloadPrefix}{DateTime.UtcNow.Ticks}",
                        cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Control] 心跳发送失败");
                    return;
                }
            }

            heartbeatTicks++;
            Interlocked.Increment(ref sentFrames);

            // 每 20 次心跳（默认约 10 分钟）一条健康汇总，便于统计会话时长分布与确认链路一直是通的
            if (heartbeatTicks % 20 == 0)
            {
                _logger.LogInformation($"[Control] 会话健康：心跳 {heartbeatTicks} 次｜RTT {RttText()}｜收帧 {recvFrames}（Swap {swapFrames}）｜发帧 {sentFrames}");
            }

            // 服务端确认支持心跳后，静默超过 3 个周期即可判定链路半开，主动断开重连
            var silent = DateTime.UtcNow - lastRecvUtc;
            if (heartbeatState > 0 && silent.TotalSeconds > Math.Max(90, interval * 3))
            {
                _logger.LogWarning($"[Control] 心跳已发出但 {(int)silent.TotalSeconds}s 未收到任何回包，判定链路半开，主动断开重连");
                try
                {
                    current.Abort();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Control] Abort 失败");
                }
                return;
            }

            // 连续 3 个周期无回显：对端大概率是老版本服务端，降级以免刷满对端日志
            if (heartbeatState == 0 && (DateTime.UtcNow - firstHeartbeatUtc).TotalSeconds > interval * 3)
            {
                heartbeatState = -1;
                _logger.LogInformation("[Control] 服务端未回应应用层心跳（可能是旧版本服务端），已停止应用层心跳，改用 WebSocket 控制帧保活");
                StopHeartbeat();
            }
        }
        finally
        {
            Interlocked.Exchange(ref heartbeatPending, 0);
        }
    }

    private void HandleHeartbeatReply(string content)
    {
        lastRecvUtc = DateTime.UtcNow;

        if (!content.StartsWith(Heartbeat.PayloadPrefix, StringComparison.Ordinal))
        {
            return;
        }

        heartbeatState = 1;

        if (!long.TryParse(content.Substring(Heartbeat.PayloadPrefix.Length).Trim(), out var ticks))
        {
            return;
        }

        var rttMs = Math.Max(0, (DateTime.UtcNow.Ticks - ticks) / TimeSpan.TicksPerMillisecond);
        lastRttMs = rttMs;
        if (rttMs >= 100)
        {
            _logger.LogInformation($"[Control] 心跳回显 RTT={rttMs}ms");
        }
        else
        {
            _logger.LogDebug($"[Control] 心跳回显 RTT={rttMs}ms");
        }
    }

    /// <summary>
    /// 优雅下线：先停心跳，再给服务端发一个 WebSocket Close 帧，最后 Abort。
    /// Close 帧是关键：服务端的读循环会当场读到“对端发送 Close 帧”，立刻注销会话并摘掉
    /// 它注册的全部站点后端。只 Abort 的话，服务端就得等 TCP 拆链报文穿过中间设备送过来，
    /// 那个报文被 NAT/防火墙吞掉时就会留一条僵尸会话（客户端早就停了，服务端 [Snap] 里
    /// 还挂着它和它的后端），只能靠 ClientIdleTimeoutSeconds 兜底。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("===== FastTunnel Client Stoping =====");
        StopHeartbeat();
        // 轮换定时器也要在这里拆：停机流程自己会发 Close 帧，留着它就是在收工路上
        // 再多一次“主动轮换”的计数与日志，把一个正常停机写成两条互斥的结束原因。
        StopRotation();

        var current = socket;
        if (current == null)
        {
            return;
        }

        // 只用 CloseOutputAsync（把 Close 帧发出去就走，不等对端回 Close）：
        // CloseAsync 还要等对方把 Close 帧回过来，链路半开时这一步会把停机预算
        // （Host 的 ShutdownTimeout 与 Docker 的 grace period）全耗光，最后还是被 SIGKILL。
        if (current.State == WebSocketState.Open)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                await current.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client shutdown", cts.Token);
                _logger.LogInformation("[Control] 已发送 Close 帧，服务端会当场注销本会话");
            }
            catch (Exception ex)
            {
                // 发不出去不算停机失败：链路本来就可能已经断了，下面 Abort 收场就行
                _logger.LogDebug(ex, "[Control] 发送 Close 帧失败，直接 Abort");
            }
        }

        current.Abort();
    }
}
