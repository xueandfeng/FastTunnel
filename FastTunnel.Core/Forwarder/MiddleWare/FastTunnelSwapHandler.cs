using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder.MiddleWare
{
    public class FastTunnelSwapHandler
    {
        // 日志类别必须是自己的：以前注入的是 ILogger<FastTunnelClientHandler>，
        // 输出里的 [SourceContext] 会把 Swap 侧的事记到控制通道处理器的名下。
        ILogger<FastTunnelSwapHandler> logger;
        FastTunnelServer fastTunnelServer;
        static int requestCount;
        static int tunnelCount;

        /// <summary>
        /// 正在转发的访客 HTTP 请求数（走 YARP 那条非 PROXY 分支）。
        /// </summary>
        public static int RequestCount => requestCount;

        /// <summary>
        /// 当前还开着的回拨隧道数（客户端拨到 1270 的 PROXY 连接）：在用的与复用池里空闲待回收的都在里面。
        /// 不复用时它跟<see cref="RequestCount"/>应该接近 1:1，请求跑完就归零；
        /// 开启复用后“在办隧道 - 在办请求”就是池里空闲可复用的隧道数
        ///（实测：50 并发 760 发跑完，在办请求 0 / 在办隧道 49，约 60 秒后被连接池回收归零）。
        /// 只看“启动以来一共建了多少条”用 <see cref="FastTunnelForwarderHttpClientFactory.TunnelsCreated"/>。
        /// </summary>
        public static int TunnelCount => tunnelCount;

        public FastTunnelSwapHandler(ILogger<FastTunnelSwapHandler> logger, FastTunnelServer fastTunnelServer)
        {
            this.logger = logger;
            this.fastTunnelServer = fastTunnelServer;
        }

        public async Task Handle(HttpContext context, Func<Task> next)
        {
            // 两个口径分开数：以前只有一个总数，而一条不复用的请求会同时占两个名额
            // （访客侧一个 + 回拨侧一个），看日志的人要么把它当隧道数读错一倍，要么自己除二。
            var isProxy = context.Request.Method == "PROXY";
            if (isProxy)
            {
                Interlocked.Increment(ref tunnelCount);
            }
            else
            {
                Interlocked.Increment(ref requestCount);
            }

            try
            {
                if (!isProxy)
                {
                    // 访客请求的最终结果在这里落一行。YARP 自己只在超时/出错时打一条不带 URL 的
                    // 日志（还连带十几行 abort 堆栈），想按站点看 5xx 根本没法查。
                    var startedMs = Environment.TickCount64;
                    try
                    {
                        await next();
                    }
                    finally
                    {
                        var statusCode = context.Response.StatusCode;
                        if (statusCode >= StatusCodes.Status500InternalServerError)
                        {
                            logger.LogWarning($"[5xx] {statusCode} {context.Request.Method}"
                                + $" {context.Request.Host.Value}{context.Request.Path}{context.Request.QueryString}"
                                + $"（耗时 {(Environment.TickCount64 - startedMs) / 1000.0:F2} 秒）");
                        }
                    }
                    return;
                }

                var requestId = (context.Request.Path.Value ?? string.Empty).Trim('/');
                logger.LogDebug($"[PROXY]:Start {requestId}");

                // 非法 RequestId（误打到 1270 的扫描器、TLS 握手、空路径）在此早退，
                // 不再把 null / 任意字符串往下传递。
                if (requestId.Length == 0 || !Guid.TryParse(requestId, out _))
                {
                    logger.LogWarning($"[PROXY]:RequestId 非法 '{Abbrev(requestId)}'，来源 {context.Connection.RemoteIpAddress}");
                    context.Response.StatusCode = 400;
                    return;
                }

                if (!fastTunnelServer.ResponseTasks.TryRemove(requestId, out var responseAwaiter))
                {
                    // 迟到回拨：等这条隧道的请求已经超时/降级走了（那条路径在 finally 里就把 msgId 摘掉了），
                    // 客户端此刻才把 PROXY 连接拨过来。这是预期路径，且 GUID 定位不了任何东西；
                    // 真正的现场由 proxyAsync 那条“Swap 失败 / 全部不可用”的 WRN 记着，这里降到 Debug。
                    logger.LogDebug($"[PROXY]:RequestId 不存在 {requestId}（对应请求已超时离开，迟到回拨）");
                    return;
                };

                var lifetime = context.Features.Get<IConnectionLifetimeFeature>();
                var transport = context.Features.Get<IConnectionTransportFeature>();

                if (lifetime == null || transport == null)
                {
                    return;
                }

                using var reverseConnection = new WebSocketStream(lifetime, transport);
                responseAwaiter.Item1.TrySetResult(reverseConnection);

                CancellationTokenSource cts;
                if (responseAwaiter.Item2 != CancellationToken.None)
                {
                    cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ConnectionClosed, responseAwaiter.Item2);
                }
                else
                {
                    cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ConnectionClosed);
                }

                var closedAwaiter = new TaskCompletionSource<object>();

                //lifetime.ConnectionClosed.Register((task) =>
                //{
                //    (task as TaskCompletionSource<object>).SetResult(null);
                //}, closedAwaiter);

                await closedAwaiter.Task.WaitAsync(cts.Token);
                logger.LogDebug($"[PROXY]:Closed {requestId}");
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex);
            }
            finally
            {
                if (isProxy)
                {
                    Interlocked.Decrement(ref tunnelCount);
                }
                else
                {
                    Interlocked.Decrement(ref requestCount);
                }
                logger.LogDebug($"[统计] 在办请求 {RequestCount}｜在办隧道 {TunnelCount}");
            }
        }
        private static string Abbrev(string text)
        {
            const int max = 100;
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            return text.Length <= max ? text : text.Substring(0, max) + "...(截断)";
        }
    }
}
