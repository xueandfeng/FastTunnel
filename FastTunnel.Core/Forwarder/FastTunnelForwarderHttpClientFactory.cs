// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Models;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Yarp.ReverseProxy.Forwarder;

namespace FastTunnel.Core.Forwarder;

public class FastTunnelForwarderHttpClientFactory : ForwarderHttpClientFactory
{
    readonly ILogger<FastTunnelForwarderHttpClientFactory> logger;
    readonly FastTunnelServer fastTunnelServer;
    private readonly IHttpContextAccessor _httpContextAccessor;
    static int connectionCount;

    /// <summary>
    /// 累计成功建立过多少条反向隧道（只增不减）。它和请求数一起看就是复用是否真的在生效：
    /// 不复用时两者应接近 1:1（实测 50 并发 500 发建 500 条），复用时应明显慢于请求数
    /// （同场景实测只建 36 条）。以前这两个数只能靠内网站点自己开 accept 计数才能看到。
    /// </summary>
    public static long TunnelsCreated => tunnelsCreated;

    static long tunnelsCreated;

    public FastTunnelForwarderHttpClientFactory(
        ILogger<FastTunnelForwarderHttpClientFactory> logger,
        IHttpContextAccessor httpContextAccessor, FastTunnelServer fastTunnelServer)
    {
        this.fastTunnelServer = fastTunnelServer;
        this.logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        base.ConfigureHandler(context, handler);
        handler.ConnectCallback = ConnectCallback;

        var serverOption = fastTunnelServer.ServerOption.CurrentValue;

        // 默认不让隧道跨请求复用。复用看起来省一次握手，但 ConnectCallback 只在“要新建连接”时才跑，
        // 请求会被直接塞进旧隧道，绕过 proxyAsync 里的多后端选路：
        // 后端半开时访问者会一路干等到响应超时（默认 100 秒）才拿到 504，切换根本轮不上
        // （实测：复用时半开第一发 504@响应超时整点，日志里连该请求的“Swap开始”都没有；不复用时同一发 3 秒内降级切走并 200）。
        if (!serverOption.WebReuseTunnel)
        {
            handler.PooledConnectionLifetime = TimeSpan.Zero;
        }

        // “内网后台多久没吐响应头”那个上限（默认 100 秒）不在这里设，
        // 它是 YARP 按集群生效的 HttpRequest.ActivityTimeout，见 FastTunnelInMemoryConfigProvider.AddWeb。
    }

    private async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.InitialRequestMessage.RequestUri.Host;

        var contextRequest = _httpContextAccessor.HttpContext;
        //var lifetime = contextRequest.Features.Get<IConnectionLifetimeFeature>()!;

        try
        {
            Interlocked.Increment(ref connectionCount);
            var res = await proxyAsync(host, context, contextRequest.RequestAborted);
            return res;
        }
        catch (Exception)
        {
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref connectionCount);
            logger.LogDebug($"统计YARP连接数：{connectionCount}");
        }
    }

    public async ValueTask<Stream> proxyAsync(string host, SocketsHttpConnectionContext context, CancellationToken cancellation)
    {
        if (!fastTunnelServer.WebList.TryGetValue(host, out var group))
        {
            // 客户端从未注册过该站点
            return await OfflinePage(host, context);
        }

        // 默认配置下每个请求都会走到这里（隧道不复用），所以半开的后端不会一直霸着访问者
        var backends = group.Candidates();
        if (backends.Count == 0)
        {
            logger.LogWarning($"[Http]站点 {host} 当前没有可用后端（客户端会话都已断开）");
            return await OfflinePage(host, context);
        }

        var waitSeconds = fastTunnelServer.ServerOption.CurrentValue.WebBackendWaitSeconds;
        var tripCount = fastTunnelServer.ServerOption.CurrentValue.WebBackendTimeoutTripCount;
        Exception? lastError = null;

        for (var i = 0; i < backends.Count; i++)
        {
            var web = backends[i];
            var msgId = Guid.NewGuid().ToString().Replace("-", "");

            TaskCompletionSource<Stream> tcs = new();

            using var requestCanceled = cancellation.Register(() =>
            {
                logger.LogDebug($"[Proxy TimeOut]:{msgId}");
                tcs.TrySetCanceled();
            });

            fastTunnelServer.ResponseTasks.TryAdd(msgId, (tcs, cancellation));

            // 每个后端单独限时：半开的控制通道发信令不会马上报错，不限时会让访问者一直转圈
            using var backendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

            // 已经降级到阈值的后端不再按完整窗口等：它可能真半开，也可能只是慢，
            // 两种情况都不该让访问者为它付满整个预算。这里只是少等一会儿，不摘除。
            var budgetSeconds = waitSeconds;
            var overTrip = tripCount > 0 && web.ConsecutiveTimeouts >= tripCount;
            if (overTrip && budgetSeconds > 1)
            {
                budgetSeconds = Math.Max(1, budgetSeconds / tripCount);
            }

            if (budgetSeconds > 0)
            {
                backendCts.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));
            }

            try
            {
                logger.LogDebug($"[Http]Swap开始 {msgId}|{host}=>{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}"
                    + $"（后端 {web.SessionId}，第 {i + 1}/{backends.Count} 个，本次等 {budgetSeconds} 秒，"
                    + $"已连续降级 {web.ConsecutiveTimeouts} 次）");

                await web.Socket.SendCmdAsync(MessageType.SwapMsg, $"{msgId}|{web.WebConfig.LocalIp}:{web.WebConfig.LocalPort}", backendCts.Token);
                var res = await tcs.Task.WaitAsync(backendCts.Token);

                // 隧道能建起来就说明这条后端是好的，之前的降级记录一笔勾销
                web.ResetTimeouts();
                Interlocked.Increment(ref tunnelsCreated);
                logger.LogDebug($"[Http]Swap OK {msgId}");
                return res;
            }
            catch (Exception ex)
            {
                lastError = ex;

                // 访问者已经取消请求，没必要再换后端试
                if (cancellation.IsCancellationRequested)
                {
                    throw;
                }

                var remaining = backends.Count - i - 1;
                var why = $"{ex.GetType().Name} {Abbrev(ex.Message)}";

                // 判据只用“控制通道是否还 Open”，不能拿异常类型当依据：
                // SocketClosedException 也会由发送排队超时抛出，此时后端其实是好的。
                if (!web.IsAlive)
                {
                    group.Drop(web);
                    logger.LogWarning($"[Http]Swap 失败 {msgId}（后端 {web.SessionId}：{why}），控制通道已关闭，摘除该后端（还剩 {remaining} 个）");
                }
                else
                {
                    // 通道还在，就当成是我们自己等急了：本次让给别人，但绝不摘除这个后端。
                    // 并发下每条业务流都要新建一条 TCP（wss 还要 TLS 握手），偶发超时完全正常；
                    // 而 Drop 是永久的（只有客户端重新登录才会 Add 回来），一旦摘掉唯一后端，
                    // 站点就变成离线页，客户端明明活着也不会自愈。
                    var times = web.RegisterTimeout();
                    var message = $"[Http]Swap 失败 {msgId}（后端 {web.SessionId}：{why}），第 {times} 次降级；"
                        + $"控制通道仍在，不摘除，本次改用其他后端（还剩 {remaining} 个）";

                    if (times >= tripCount)
                    {
                        message += $"，已达阈值 {tripCount}，之后的请求对它只等 {Math.Max(1, waitSeconds / tripCount)} 秒";
                    }

                    logger.LogWarning(message);
                }
            }
            finally
            {
                fastTunnelServer.ResponseTasks.TryRemove(msgId, out _);
                tcs.TrySetCanceled(CancellationToken.None);
            }
        }

        logger.LogWarning($"[Http]站点 {host} 的 {backends.Count} 个后端全部不可用："
            + $"{lastError?.GetType().Name} {Abbrev(lastError?.Message)}");
        return await OfflinePage(host, context);
    }

    private static string Abbrev(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        const int max = 120;
        return text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "...(截断)");
    }

    /// <summary>
    /// 没有可用后端时给访客的页面。
    /// 状态码用 503 而不是 200：回 200 时监控与客户端都把“站点挂了”当成请求成功，
    /// 半开、后端全挂这类故障在访问日志里只留下一条 200，完全不报警。
    /// 同时把 Content-Length 与 Connection: close 写全：这段报文是从 ConnectCallback 里手写的，
    /// 不写长度就靠连接关闭定界，部分客户端会把一个 503 当成“异常中断”而默默重试。
    /// </summary>
    private async ValueTask<Stream> OfflinePage(string host, SocketsHttpConnectionContext context)
    {
        var body = Encoding.UTF8.GetBytes($"{TunnelResource.Page_Offline}\r\n");
        var head = Encoding.UTF8.GetBytes(
            "HTTP/1.1 503 Service Unavailable\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Connection: close\r\n\r\n");

        var bytes = new byte[head.Length + body.Length];
        head.CopyTo(bytes, 0);
        body.CopyTo(bytes, head.Length);

        return await Task.FromResult(new ResponseStream(bytes));
    }
}
