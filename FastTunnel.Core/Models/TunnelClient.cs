// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Utilitys;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Models;

public class TunnelClient
{
    public WebSocket webSocket { get; private set; }

    /// <summary>
    /// 会话编号，日志用（同一 IP 有多条连接时靠它区分）
    /// </summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);

    /// <summary>
    /// 服务端端口号
    /// </summary>
    public int ConnectionPort { get; set; }

    /// <summary>
    /// 对端端口（与 RemoteIpAddress 合起来才能定位到具体一条 TCP 连接）
    /// </summary>
    public int RemotePort { get; set; }

    private readonly FastTunnelServer fastTunnelServer;
    private readonly ILoginHandler loginHandler;
    private readonly ILogger<TunnelClient> logger;

    public IPAddress RemoteIpAddress { get; private set; }

    /// <summary>本会话收到的控制帧总数。</summary>
    private long controlFrames;

    /// <summary>其中应用层心跳帧数。</summary>
    private long heartbeatFrames;

    /// <summary>登录帧数，大于 1 则说明对端在同一连接上重发登录。</summary>
    private long loginFrames;

    /// <summary>其它类型控制帧数（Swap/Forward/Log/未知）。</summary>
    private long otherFrames;

    /// <summary>最后一条控制帧的命令类型，用于判断断线前正在做什么。</summary>
    public string LastFrameType { get; private set; } = "none";

    /// <summary>登录是否已处理完成（决定注销时要不要提醒“会话从未注册任何隧道”）。</summary>
    public bool LoginCompleted { get; internal set; }

    public readonly IList<WebInfo> WebInfos = new List<WebInfo>();
    public readonly IList<ForwardInfo<ForwardHandlerArg>> ForwardInfos = new List<ForwardInfo<ForwardHandlerArg>>();

    public TunnelClient(
        WebSocket webSocket, FastTunnelServer fastTunnelServer,
        ILoginHandler loginHandler, IPAddress remoteIpAddress, ILogger<TunnelClient> logger)
    {
        this.logger = logger;
        this.webSocket = webSocket;
        this.fastTunnelServer = fastTunnelServer;
        this.loginHandler = loginHandler;
        this.RemoteIpAddress = remoteIpAddress;
        this.StartTime = DateTime.Now;
    }

    public DateTime StartTime { get; }

    /// <summary>
    /// 最近一次收到该客户端任意控制帧的时刻（UTC），含应用层心跳。
    /// 用于清理因链路静默断开而残留的僵尸会话。
    /// </summary>
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>是否已被服务端主动终止。</summary>
    public bool IsAborted { get; private set; }

    internal void AddWeb(WebInfo info)
    {
        WebInfos.Add(info);
    }

    internal void AddForward(ForwardInfo<ForwardHandlerArg> forwardInfo)
    {
        ForwardInfos.Add(forwardInfo);
    }

    /// <summary>
    /// 接收客户端的消息。会话摘要在读循环退出后必写，正常退出与读循环抛异常都写。
    /// </summary>
    /// <param name="cancellationToken">Kestrel 的请求取消令牌（对端拆链与本进程停机都会触发）。</param>
    /// <param name="stoppingToken">宿主停机令牌，用来把上面两种情况分开。</param>
    public async Task ReviceAsync(CancellationToken cancellationToken, CancellationToken stoppingToken = default)
    {
        var utility = new WebSocketUtility(webSocket, ProcessLine);

        try
        {
            await utility.ProcessLinesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // 读循环里没被 WebSocketUtility 兜住的异常（例如 Pipe 自身出错）。摘要照写，
            // 异常原样向上抛，调用方的处理路径不变。
            WriteSessionSummary(utility, cancellationToken, stoppingToken, ex);
            throw;
        }

        WriteSessionSummary(utility, cancellationToken, stoppingToken, null);
    }

    /// <summary>
    /// 会话结束的完整摘要（写进文件日志，不靠现场抓控制台）。
    /// </summary>
    private void WriteSessionSummary(WebSocketUtility utility, CancellationToken cancellationToken,
        CancellationToken stoppingToken, Exception? escaped)
    {
        var online = DateTime.Now - StartTime;
        var lastInbound = (int)(DateTime.UtcNow - LastActivityUtc).TotalSeconds;
        // 取消来源是这类排障里最关心的一项：context.RequestAborted 在“对端拆链”和“本进程正在停机”
        // 两种场合都会触发，只看它分不开，所以要拿停机令牌再判一次。判错方向的代价很大：
        // 把中间设备周期性拆链当成自己重启，就会去查一个根本没发生过的发布。
        // 不传 stoppingToken（默认值）时无法区分，只能报“未取消”或“取消来源未接入”。
        string cancelSource;
        if (!cancellationToken.IsCancellationRequested)
        {
            cancelSource = "无(读循环自行退出)";
        }
        else if (stoppingToken.CanBeCanceled)
        {
            cancelSource = stoppingToken.IsCancellationRequested ? "本机正在停机" : "对端拆链(本进程未停机)";
        }
        else
        {
            cancelSource = "未接入停机令牌，无法区分";
        }

        // WS 状态是独立的一路证据：CloseReceived/CloseSent 说明走了关闭握手，
        // Aborted 说明是被硬拆（本机 Abort 或链路异常），Open 则说明读循环退出时连接看着还活着。
        // 走了关闭握手的收尾记 INFO：客户端开了会话轮换之后，对端正常发 Close 会每一轮都出一条，
        // 再按 WRN 记就是把计划内动作混进真故障里（旁边那条“客户端关闭”仍报 WRN，
        // 它说的是“少了一个后端”这个业务影响，两回事）。
        var graceful = IsAborted
            || (escaped == null
                && (webSocket.State == WebSocketState.CloseReceived
                    || webSocket.State == WebSocketState.CloseSent
                    || webSocket.State == WebSocketState.Closed));
        var level = graceful ? LogLevel.Information : LogLevel.Warning;
        var tail = escaped == null ? "" : $"｜未捕获异常 {escaped.GetType().Name}: {Abbrev(escaped.Message)}";

        logger.Log(level, $"[Session {SessionId}] 控制通道结束：{utility.EndReason}｜取消来源 {cancelSource}｜WS状态 {webSocket.State}｜"
            + $"对端 {RemoteIpAddress}:{RemotePort}｜本地端口 {ConnectionPort}｜"
            + $"在线 {FormatSpan(online)}｜控制帧 {controlFrames}（心跳 {heartbeatFrames}/登录 {loginFrames}/其它 {otherFrames}）｜"
            + $"最后帧 {LastFrameType}｜注册站点 {WebInfos.Count}｜端口转发 {ForwardInfos.Count}｜登录完成 {LoginCompleted}｜"
            + $"距上次入站 {lastInbound}s 前{tail}");
    }

    internal static string FormatSpan(TimeSpan span)
    {
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}小时{span.Minutes:00}分{span.Seconds:00}秒"
            : $"{span.Minutes:00}分{span.Seconds:00}秒";
    }

    private void ProcessLine(ReadOnlySequence<byte> line, CancellationToken cancellationToken)
    {
        LastActivityUtc = DateTime.UtcNow;
        controlFrames++;

        string cmd;
        try
        {
            cmd = Encoding.UTF8.GetString(line);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "解析客户端消息失败");
            return;
        }

        if (cmd.Length == 0)
        {
            return;
        }

        var type = (MessageType)cmd[0];
        LastFrameType = type.ToString();

        if (type == MessageType.HeartBeat)
        {
            heartbeatFrames++;
            // 应用层心跳：原样回显负载（"hb {ticks}"），客户端据此算 RTT 并判定链路是否半开。
            // 必须在交给 loginHandler 之前处理，否则会被当成登录 JSON 去反序列化。
            _ = EchoHeartbeatAsync(cmd.Substring(1), cancellationToken);
            return;
        }

        if (type == MessageType.LogIn)
        {
            loginFrames++;
            if (loginFrames > 1)
            {
                logger.LogWarning($"[Session {SessionId}] 同一连接上收到第 {loginFrames} 次登录指令，重复登录会重建监听器并顶掉旧会话");
            }
        }
        else
        {
            otherFrames++;
        }

        _ = HandleCmdAsync(this, cmd, cancellationToken);
    }

    private async Task EchoHeartbeatAsync(string payload, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await webSocket.SendCmdAsync(MessageType.HeartBeat, payload, cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "心跳回显失败");
        }
    }

    private async Task<bool> HandleCmdAsync(TunnelClient tunnelClient, string lineCmd, CancellationToken cancellationToken)
    {
        try
        {
            return await loginHandler.HandlerMsg(fastTunnelServer, tunnelClient, lineCmd.Substring(1), cancellationToken);
        }
        catch (Exception ex)
        {
            // 非 FastTunnel 协议的流量（端口扫描、TLS 握手、空行探测）会落到这里，
            // 只记一条可定位的警告，不把整个异常对象含堆栈刷进日志。
            logger.LogWarning(ex, $"[Session {SessionId}] 处理客户端消息失败：{Abbrev(lineCmd)}");
            return false;
        }
    }

    private static string Abbrev(string text)
    {
        if (text == null)
        {
            return "";
        }

        const int max = 200;
        return text.Length <= max ? text : text.Substring(0, max) + "...(截断)";
    }

    /// <summary>
    /// 强制终止会话（不发送 Close 帧），用于清理僵尸客户端。
    /// Abort 会让在读循环里阻塞的 ReceiveAsync 抛异常，从而触发正常的注销流程。
    /// </summary>
    internal void Abort(string reason)
    {
        if (IsAborted)
        {
            return;
        }

        IsAborted = true;
        var online = DateTime.Now - StartTime;
        logger.LogWarning($"[Session {SessionId}] 主动终止会话（{reason}）｜对端 {RemoteIpAddress}:{RemotePort}｜本次在线 {FormatSpan(online)}｜控制帧 {controlFrames}");

        try
        {
            webSocket.Abort();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Abort 客户端会话失败");
        }
    }

    internal void Logout()
    {
        // 站点后端注销：必须把本会话从路由表摘掉。留着它，后续请求会被送向这条已关闭的
        // 控制通道，访问者看到的是一个异常而不是干净的离线页。
        if (WebInfos != null)
        {
            foreach (var item in WebInfos)
            {
                try
                {
                    fastTunnelServer.UnregisterWeb(item.Domain, SessionId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"[Session {SessionId}] 注销站点后端 {item.Domain} 失败：{ex.Message}");
                }
            }
        }

        // forward监听终止
        if (ForwardInfos != null)
        {
            foreach (var item in ForwardInfos)
            {
                try
                {
                    fastTunnelServer.ForwardList.TryRemove(item.SSHConfig.RemotePort, out _);
                    item.Listener.Stop();
                    logger.LogInformation($"[Session {SessionId}] 释放端口转发监听 {item.SSHConfig.RemotePort} => {item.SSHConfig.LocalIp}:{item.SSHConfig.LocalPort}");
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"[Session {SessionId}] 释放端口转发监听 {item.SSHConfig.RemotePort} 失败：{ex.Message}");
                }
            }
        }

        if (!LoginCompleted)
        {
            logger.LogWarning($"[Session {SessionId}] 会话结束但未完成登录（控制帧 {controlFrames}，最后帧 {LastFrameType}），"
                + "可能是探测流量、版本/Token 不兼容，或登录指令在断开前来不及处理");
        }

        try
        {
            _ = webSocket.CloseAsync(WebSocketCloseStatus.Empty, "", CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "关闭客户端 WebSocket 失败");
        }
    }
}
