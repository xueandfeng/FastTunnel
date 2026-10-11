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
        /// 隧道交还给 Kestrel 之前，最多再等多久让使用方（YARP 的连接、端口转发、UDP 会话）
        /// 把读写彻底脱离这条连接的管道。
        ///
        /// 为什么必须等：<see cref="WebSocketStream"/> 的说明——中间件一返回，Kestrel 就可能
        /// 在同一条连接上发起“读下一个请求”，而此时若还有读者挂在 Transport.Input 上，两者并发
        /// 就抛 <c>Concurrent reads or writes are not supported.</c>，日志里对应
        /// <c>Connection processing ended abnormally.</c>。等到“读写已脱离”再返回，这个并发窗口就不存在了。
        /// 平时这一步是即刻完成的（使用方 Dispose 时读循环就会被叫醒），只有对端卡住才会走到超时。
        /// </summary>
        static readonly TimeSpan TunnelReleaseGrace = TimeSpan.FromSeconds(5);

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

                var reverseConnection = new WebSocketStream(lifetime, transport);
                try
                {
                    if (!responseAwaiter.Item1.TrySetResult(reverseConnection)
                        && !responseAwaiter.Item1.Task.IsCompletedSuccessfully)
                    {
                        // 无人接手：等这条隧道的请求已经取消/超时走了（proxyAsync 的 finally 会
                        // TrySetCanceled），客户端此刻才把 PROXY 连接拨过来。孤儿隧道没有使用方，
                        // 不能让它空挂在这条连接上，交给 finally 掐断。
                        logger.LogDebug($"[PROXY]:无人接手 {requestId}（对应请求已离开），断开这条隧道");
                        return;
                    }

                    using var cts = responseAwaiter.Item2 != CancellationToken.None
                        ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.ConnectionClosed, responseAwaiter.Item2)
                        : CancellationTokenSource.CreateLinkedTokenSource(lifetime.ConnectionClosed);

                    // 等的是“使用方已经把读写脱离管道”，不是只等取消信号——见 TunnelReleaseGrace。
                    await reverseConnection.Released.WaitAsync(cts.Token);
                    logger.LogDebug($"[PROXY]:Closed {requestId}");
                }
                catch (OperationCanceledException)
                {
                    // 访客取消 / 隧道被拆：正常的收尾路径，不算故障
                    logger.LogDebug($"[PROXY]:Cancel {requestId}");
                }
                finally
                {
                    // 不管从哪个分支出去（正常释放、取消、无人接手），都要在把连接交回 Kestrel
                    // 之前确认读写已脱离管道；等不到就记一条日志——Abort 已经发出去，这条连接会被
                    // Kestrel 直接拆掉，不会再有“读下一个请求”的机会。
                    if (!await reverseConnection.ShutdownAsync(TunnelReleaseGrace))
                    {
                        logger.LogWarning($"[PROXY]:隧道 {requestId} 在 {(int)TunnelReleaseGrace.TotalSeconds} 秒内"
                            + "仍未脱离连接管道（使用方还占着读写），已强制断开这条连接");
                    }
                }
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
