// Copyright (c) 2019-2022 Gui.H. https://github.com/FastTunnel/FastTunnel
// The FastTunnel licenses this file to you under the Apache License Version 2.0.
// For more details,You may obtain License file at: https://github.com/FastTunnel/FastTunnel/blob/v2/LICENSE

using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Handlers.Server;
using FastTunnel.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder.MiddleWare
{
    public class FastTunnelClientHandler
    {
        readonly ILogger<FastTunnelClientHandler> logger;
        readonly FastTunnelServer fastTunnelServer;
        readonly Version serverVersion;
        readonly ILoginHandler loginHandler;

        static int connectionCount;

        public static int ConnectionCount => connectionCount;

        public FastTunnelClientHandler(
            ILogger<FastTunnelClientHandler> logger, FastTunnelServer fastTunnelServer, ILoginHandler loginHandler)
        {
            this.logger = logger;
            this.fastTunnelServer = fastTunnelServer;
            this.loginHandler = loginHandler;

            serverVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        }

        public async Task Handle(HttpContext context, Func<Task> next)
        {
            try
            {
                if (!context.WebSockets.IsWebSocketRequest || !context.Request.Headers.TryGetValue(FastTunnelConst.FASTTUNNEL_VERSION, out var version))
                {
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        // 是 WS 握手但缺 FastTunnel 版本头：浏览器页面、探测或其它隧道工具连上 1270 会落到这里
                        logger.LogWarning($"[Handshake] 收到 WS 握手但缺少 {FastTunnelConst.FASTTUNNEL_VERSION} 头，来向 {context.Connection.RemoteIpAddress}:{context.Connection.RemotePort}，已交给下游处理");
                    }

                    await next();
                    return;
                };

                Interlocked.Increment(ref connectionCount);

                try
                {
                    await handleClient(context, version);
                }
                finally
                {
                    Interlocked.Decrement(ref connectionCount);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex);
            }
        }

        private async Task handleClient(HttpContext context, string clientVersion)
        {
            var remote = $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort}";
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

            logger.LogInformation($"[Handshake] WS 控制通道已建立 {remote} → 本地端口 {context.Connection.LocalPort}，客户端版本 {clientVersion}，服务端版本 {serverVersion}");

            // 用 TryParse 替代 Parse：非 FastTunnel 客户端（扫描器、curl、TLS 探测）发来的
            // 版本头不再抛 FormatException，而是按版本不兼容正常关闭。
            if (!Version.TryParse(clientVersion, out var versionInfo) || versionInfo.Major != serverVersion.Major)
            {
                logger.LogWarning($"[Handshake] 拒绝 {remote}：客户端版本 {clientVersion} 不合法或不匹配服务端 {serverVersion}");
                await Close(webSocket, $"客户端版本{clientVersion}与服务端版本{serverVersion}不兼容，请升级。");
                return;
            }

            if (!checkToken(context))
            {
                logger.LogWarning($"[Handshake] 拒绝 {remote}：Token 验证失败（版本 {clientVersion}），请核对两端 Token 配置");
                await Close(webSocket, "Token验证失败");
                return;
            }

            var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
            var log = loggerFactory.CreateLogger<TunnelClient>();
            var client = new TunnelClient(webSocket, fastTunnelServer, loginHandler, context.Connection.RemoteIpAddress, log);
            client.ConnectionPort = context.Connection.LocalPort;
            client.RemotePort = context.Connection.RemotePort;

            try
            {
                fastTunnelServer.ClientLogin(client);
                await client.ReviceAsync(context.RequestAborted);
            }
            catch (Exception ex)
            {
                // 这里只会出现读循环本身的链路异常；固定 Errno 信息已记在会话摘要里，
                // 只记一条简短警告，避免扫描流量把完整堆栈刷满日志。
                logger.LogWarning($"[Session {client.SessionId}] 控制通道异常终止：{ex.GetType().Name}: {Abbrev(ex.Message)}");
            }
            finally
            {
                fastTunnelServer.ClientLogout(client);
            }
        }

        private static string Abbrev(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            const int max = 300;
            return text.Length <= max ? text : text.Substring(0, max) + "...(截断)";
        }

        private static async Task Close(WebSocket webSocket, string reason)
        {
            try
            {
                await webSocket.SendCmdAsync(MessageType.Log, reason, CancellationToken.None);
                await webSocket.CloseAsync(WebSocketCloseStatus.Empty, string.Empty, CancellationToken.None);
            }
            catch (Exception)
            {
                // 对端可能已经断开，发送/关闭失败不必再抛一层异常刷日志
            }
        }

        private bool checkToken(HttpContext context)
        {
            var checkToken = false;
            if (fastTunnelServer.ServerOption.CurrentValue.Tokens != null && fastTunnelServer.ServerOption.CurrentValue.Tokens.Count != 0)
            {
                checkToken = true;
            }

            if (!checkToken)
                return true;

            // 客户端未携带token，登录失败
            if (!context.Request.Headers.TryGetValue(FastTunnelConst.FASTTUNNEL_TOKEN, out var token))
                return false;

            if (fastTunnelServer.ServerOption.CurrentValue.Tokens?.Contains(token) ?? false)
                return true;

            return false;
        }
    }
}
