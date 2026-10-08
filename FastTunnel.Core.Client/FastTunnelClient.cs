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

#if NET6_0_OR_GREATER
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(LogInMassage))]
[JsonSerializable(typeof(Message<TunnelMassage>))]
public partial class SourceGenerationContext : JsonSerializerContext
{
}
#endif

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

    /// <summary>0 = 尚未确认，1 = 服务端支持应用层心跳，-1 = 服务端不支持（已降级）。</summary>
    private int heartbeatState;

    private DateTime lastRecvUtc = DateTime.UtcNow;
    private DateTime firstHeartbeatUtc;
    private int consecutiveFailures;

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

            try
            {
                await loginAsync(cancellationToken);
                StartHeartbeat();
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
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var alive = DateTime.UtcNow - startUtc;

            // 能撑过 1 分钟的会话说明不是本地反复闪断，多半是链路中间设备把会话回收了，
            // 重连间隔退回基础值；只有在短时间内连续掉线才做指数退避。
            consecutiveFailures = alive.TotalSeconds >= 60 ? 0 : consecutiveFailures + 1;
            var delay = NextReconnectDelaySeconds(consecutiveFailures);
            var reason = DescribeEnd(fault);
            var silent = (int)(DateTime.UtcNow - lastRecvUtc).TotalSeconds;
            var stats = $"收帧 {recvFrames}（Swap {swapFrames}）｜发帧 {sentFrames}（心跳 {heartbeatTicks}）｜RTT {RttText()}｜距上次入站 {silent}s 前";

            if (alive.TotalSeconds >= 60)
            {
                _logger.LogWarning($"[Control] 会话结束：{reason}｜本次在线 {FormatAlive(alive)}｜{stats}｜{delay}s 后重连");
            }
            else
            {
                _logger.LogError($"[Control] 会话结束：{reason}｜本次在线 {FormatAlive(alive)}｜{stats}｜连续快速掉线 {consecutiveFailures + 1} 次｜{delay}s 后重连");
            }

            if (fault != null)
            {
                _logger.LogDebug(fault, "[Control] 会话异常详情");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
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
            + $"应用层心跳 {(ClientConfig.HeartbeatSeconds > 0 ? ClientConfig.HeartbeatSeconds + "s" : "已关闭")}｜本地版本 {AssemblyUtility.GetVersion()}");
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

# if NET8_0_OR_GREATER
        return new LogInMassage
        {
            Webs = ClientConfig.Webs,
            Forwards = ClientConfig.Forwards,
        }.ToJson(jsonTypeInfo: SourceGenerationContext.Default.LogInMassage);

#else
        return new LogInMassage
        {
            Webs = ClientConfig.Webs,
            Forwards = ClientConfig.Forwards,
        }.ToJson();
#endif
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

#if NETCOREAPP3_1
            var content = Encoding.UTF8.GetString(line.Slice(1).ToArray());
#endif

#if NET5_0_OR_GREATER
            var content = Encoding.UTF8.GetString(line.Slice(1));
#endif
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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("===== FastTunnel Client Stoping =====");
        StopHeartbeat();
        if (socket != null)
        {
            socket.Abort();
        }
    }
}
