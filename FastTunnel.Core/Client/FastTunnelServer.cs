// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Config;
using FastTunnel.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Options;
using System.IO;
using Yarp.ReverseProxy.Configuration;
using FastTunnel.Core.Forwarder;
using System.Collections.Generic;
using FastTunnel.Core.Forwarder.MiddleWare;

namespace FastTunnel.Core.Client
{
    public class FastTunnelServer : IDisposable
    {
        public readonly IOptionsMonitor<DefaultServerConfig> ServerOption;
        public IProxyConfigProvider proxyConfig;
        readonly ILogger<FastTunnelServer> logger;

        public ConcurrentDictionary<string, (TaskCompletionSource<Stream>, CancellationToken)> ResponseTasks { get; } = new();

        /// <summary>
        /// 站点路由表：域名 → 该域名的所有客户端后端。多个客户端实例同时登录时不会互相顶掉，
        /// 而是都挂在这里，由选路逻辑挑存活的那一个。
        /// </summary>
        public ConcurrentDictionary<string, WebBackendGroup> WebList { get; private set; } = new();

        public ConcurrentDictionary<int, ForwardInfo<ForwardHandlerArg>> ForwardList { get; private set; }
            = new ConcurrentDictionary<int, ForwardInfo<ForwardHandlerArg>>();

        /// <summary>
        /// 在线客户端集合。登录/注销/空闲清理都会并发访问，旧的 List&lt;TunnelClient&gt;
        /// 在多线程 Add/Remove 下会损坏甚至抛异常，这里换成 <see cref="ConcurrentDictionary{TKey, TValue}"/>。
        /// </summary>
        private readonly ConcurrentDictionary<TunnelClient, byte> clients = new();

        /// <summary>
        /// 在线客户端列表（快照，可安全枚举）
        /// </summary>
        public IEnumerable<TunnelClient> Clients => clients.Keys;

        /// <summary>
        /// 当前在线客户端数。直接以集合为准，避免计数器与实际会话漂移。
        /// </summary>
        public int ConnectedClientCount => clients.Count;

        private readonly Timer diagTimer;
        private DateTime lastSnapshotUtc = DateTime.MinValue;

        public FastTunnelServer(ILogger<FastTunnelServer> logger, IProxyConfigProvider proxyConfig, IOptionsMonitor<DefaultServerConfig> serverSettings)
        {
            this.logger = logger;
            this.ServerOption = serverSettings;
            this.proxyConfig = proxyConfig;

            // 周期任务：僵尸会话清理 + 链路健康快照。两者共用一个定时器，避免多一套生命周期管理。
            // 固定 5 秒一发，而不是把定时器周期直接设成 DiagnosticsLogIntervalSeconds：
            // 先前定时器周期写死 30 秒、配置值只用作守卫阈值，结果快照间隔只能配成 30 的整数倍
            //（配 15 实测就是 30，本轮在测试床上看到的），配 10 也一样是 30；
            // 排查时像是日志丢了。现在周期只决定粒度（5 秒），
            // 真正的间隔由 Tick() 里的守卫控制；清空闲会话本身很便宜，扫频一点只有好处。
            diagTimer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        private void Tick()
        {
            CloseIdleClients();

            var interval = ServerOption.CurrentValue.DiagnosticsLogIntervalSeconds;
            if (interval <= 0)
            {
                return;
            }

            // 减 1 秒是给定时器抖动的余量：定时器回调会比标称时刻早上几毫秒，
            // 不留余量就会“隔一次才写一次”，又变成两倍间隔。
            if ((DateTime.UtcNow - lastSnapshotUtc).TotalSeconds < interval - 1)
            {
                return;
            }

            lastSnapshotUtc = DateTime.UtcNow;
            WriteSnapshot();
        }

        /// <summary>
        /// 一条全局快照，用于判断“不定时重连”当时到底有多少会话、哪条空闲得最久。
        /// </summary>
        private void WriteSnapshot()
        {
            try
            {
                var backendCount = 0;
                foreach (var group in WebList.Values)
                {
                    backendCount += group.Count;
                }

                // 字段口径（这几个名字被误读过，写清楚）：
                // 会话 = 已登录的客户端会话；控制通道 = 当前打开的 WebSocket 回拨通道（含登录后未完成的）；
                // 在办请求 = 服务端正在转发的访客 HTTP 请求；在办隧道 = 当前还开着的回拨隧道（在用 + 池内空闲）；
                //   不复用时一请求一隧道，这两个数应该接近相等，请求跑完一起归零；
                //   开复用后“在办隧道 - 在办请求”就是池里空闲可复用的隧道数（约 60 秒无访问后被回收）；
                // 累计隧道 = 启动以来建过多少条，与请求量一起看就知道复用到底有没有生效。
                logger.LogInformation($"[Snap] 会话 {ConnectedClientCount}｜控制通道 {FastTunnelClientHandler.ConnectionCount}｜"
                    + $"在办请求 {FastTunnelSwapHandler.RequestCount}｜在办隧道 {FastTunnelSwapHandler.TunnelCount}｜"
                    + $"累计隧道 {FastTunnelForwarderHttpClientFactory.TunnelsCreated}｜"
                    + $"ResponseTasks {ResponseTasks.Count}｜站点 {WebList.Count}（后端 {backendCount}）｜ForwardList {ForwardList.Count}");

                foreach (var client in clients.Keys)
                {
                    logger.LogInformation($"[Snap]   Session {client.SessionId} {client.RemoteIpAddress}:{client.RemotePort} 本地{client.ConnectionPort} ｜"
                        + $"在线 {TunnelClient.FormatSpan(DateTime.Now - client.StartTime)}｜空闲 {(int)(DateTime.UtcNow - client.LastActivityUtc).TotalSeconds}s｜"
                        + $"站点 {client.WebInfos.Count}｜转发 {client.ForwardInfos.Count}｜最后帧 {client.LastFrameType}｜登录完成 {client.LoginCompleted}");
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "写链路快照失败");
            }
        }

        /// <summary>
        /// 踢掉长时间没有任何控制帧的客户端。仅在 ClientIdleTimeoutSeconds &gt; 0 时生效。
        /// </summary>
        internal void CloseIdleClients()
        {
            try
            {
                var timeoutSeconds = ServerOption.CurrentValue.ClientIdleTimeoutSeconds;
                if (timeoutSeconds <= 0 || clients.IsEmpty)
                {
                    return;
                }

                var deadline = DateTime.UtcNow.AddSeconds(-timeoutSeconds);
                foreach (var client in clients.Keys)
                {
                    if (client.LastActivityUtc < deadline)
                    {
                        var idleSeconds = (int)(DateTime.UtcNow - client.LastActivityUtc).TotalSeconds;
                        client.Abort($"已 {idleSeconds}s 未收到任何控制帧，超时阈值 {timeoutSeconds}s");
                    }
                }
            }
            catch (Exception ex)
            {
                // 定时器回调里的异常无人捕获，会把进程带倒，这里必须兜住
                logger.LogError(ex, "清理空闲客户端失败");
            }
        }

        /// <summary>
        /// 客户端登录
        /// </summary>
        /// <param name="client"></param>
        internal void ClientLogin(TunnelClient client)
        {
            clients[client] = 0;
            logger.LogInformation($"[Session {client.SessionId}] 客户端连接 {client.RemoteIpAddress}:{client.RemotePort} 本地端口 {client.ConnectionPort}｜"
                + $"当前会话数：{ConnectedClientCount}｜控制通道 {FastTunnelClientHandler.ConnectionCount}｜在办请求 {FastTunnelSwapHandler.RequestCount}｜在办隧道 {FastTunnelSwapHandler.TunnelCount}");
        }

        /// <summary>
        /// 注册（或刷新）一个站点后端。同一会话重复登录只保留最新一条。
        /// 域名不合法时返回 null（调用方跳过该站点，不能把它交给 YARP）。
        /// </summary>
        internal WebInfo? RegisterWeb(string hostName, WebConfig config, TunnelClient client)
        {
            // 非法域名必须在这里就拦住：一旦把它交给 YARP，整份新路由配置都会应用失败，
            // 所有站点的注册都会被一个客户端的错配置连累。
            if (!IsValidDomain(hostName))
            {
                logger.LogWarning($"[Session {client.SessionId}] 站点域名不合法，已拒绝注册：'{Abbrev(hostName)}'"
                    + "（客户端配置的 SubDomain / WWW 只能是字母、数字、连字符和点，不能含空格、冒号、下划线等；"
                    + "而且必须是完整域名 —— 至少两级、中间含点，SubDomain 拼 WebDomain 那种会自动拼上，但 WWW 得自己写全）");
                return null;
            }

            var info = new WebInfo
            {
                Socket = client.webSocket,
                WebConfig = config,
                Domain = hostName,
                SessionId = client.SessionId,
                Priority = config.Priority,
                RegisteredUtc = DateTime.UtcNow
            };

            var group = WebList.GetOrAdd(hostName, name => new WebBackendGroup(name));
            var count = group.Add(info);
            (proxyConfig as FastTunnelInMemoryConfigProvider)?.AddWeb(hostName, ServerOption.CurrentValue.WebResponseTimeoutSeconds);

            if (count > 1)
            {
                logger.LogInformation($"[Session {client.SessionId}] 站点 {hostName} 新增一个后端，当前 {count} 个后端互为备份");
            }

            return info;
        }

        /// <summary>
        /// 域名合法性校验：标签仅允许字母、数字、连字符，至少两级，不含下划线。
        /// 大小写都放行（旧版允许大写，不能因为这次加固把已经在跑的站点挡掉）。
        /// </summary>
        private static bool IsValidDomain(string host)
        {
            if (string.IsNullOrEmpty(host) || host.Length > 253 || host.StartsWith(".") || host.EndsWith(".")
                || host.Contains("..") || host.IndexOf('.') < 0)
            {
                return false;
            }

            foreach (var ch in host)
            {
                if (!(ch >= 'a' && ch <= 'z' || ch >= 'A' && ch <= 'Z' || ch >= '0' && ch <= '9' || ch == '-' || ch == '.'))
                {
                    return false;
                }
            }

            foreach (var label in host.Split('.'))
            {
                if (label.Length == 0 || label.Length > 63 || label.StartsWith("-") || label.EndsWith("-"))
                {
                    return false;
                }
            }

            return true;
        }

        private static string Abbrev(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            const int max = 120;
            return text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "...(截断)");
        }

        /// <summary>
        /// 会话注销时摘掉它注册的后端。这一步是必需的：残留的后端指向已关闭的 WebSocket，
        /// 请求会被送向死连接，访问者直接看到异常而不是干净的离线页。
        /// </summary>
        internal int UnregisterWeb(string hostName, string sessionId)
        {
            if (!WebList.TryGetValue(hostName, out var group))
            {
                return 0;
            }

            var removed = group.RemoveSession(sessionId);
            if (removed > 0)
            {
                logger.LogInformation($"[Session {sessionId}] 注销站点后端 {hostName}，剩余 {group.Count} 个"
                    + (group.Count == 0 ? "（该站点已无可用前端，将返回离线页）" : "（请求自动走其余后端）"));
            }

            return removed;
        }

        /// <summary>
        /// 客户端退出
        /// </summary>
        /// <param name="client"></param>
        internal void ClientLogout(TunnelClient client)
        {
            var removed = clients.TryRemove(client, out _);
            if (removed)
            {
                logger.LogWarning($"[Session {client.SessionId}] 客户端关闭  {client.RemoteIpAddress}:{client.RemotePort}｜本次在线 {TunnelClient.FormatSpan(DateTime.Now - client.StartTime)}｜"
                    + $"当前会话数：{ConnectedClientCount}｜控制通道 {FastTunnelClientHandler.ConnectionCount - 1}｜在办请求 {FastTunnelSwapHandler.RequestCount}｜在办隧道 {FastTunnelSwapHandler.TunnelCount}");
            }
            else
            {
                logger.LogDebug($"[Session {client.SessionId}] 注销时已不在在线会话列表（重复注销）");
            }

            client.Logout();
        }

        public void Dispose()
        {
            diagTimer?.Dispose();
        }
    }
}
