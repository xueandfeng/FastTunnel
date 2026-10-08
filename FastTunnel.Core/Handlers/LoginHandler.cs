// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using FastTunnel.Core.Extensions;
using FastTunnel.Core.Forwarder;
using FastTunnel.Core.Listener;
using FastTunnel.Core.Models;
using FastTunnel.Core.Models.Massage;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;

namespace FastTunnel.Core.Handlers.Server;


[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(LogInMassage))]
internal partial class SourceGenerationContext : JsonSerializerContext
{
}

public class LoginHandler : ILoginHandler
{
    private readonly ILogger logger;
    private readonly IProxyConfigProvider proxyConfig;
    public const bool NeedRecive = true;

    public LoginHandler(ILogger<LoginHandler> logger, IProxyConfigProvider proxyConfig)
    {
        this.proxyConfig = proxyConfig;
        this.logger = logger;
    }

    protected async Task HandleLoginAsync(FastTunnelServer server, TunnelClient client, LogInMassage requet, CancellationToken cancellationToken)
    {
        var hasTunnel = false;

        var tips = new List<string>();

        await client.webSocket.SendCmdAsync(MessageType.Log, $"穿透协议 | 映射关系（公网=>内网）", cancellationToken);
        await Task.Delay(300, cancellationToken);

        if (requet.Webs != null && requet.Webs.Any())
        {
            hasTunnel = true;
            foreach (var item in requet.Webs)
            {
                var hostName = $"{item.SubDomain}.{server.ServerOption.CurrentValue.WebDomain}".Trim().ToLower();

                logger.LogDebug($"new domain '{hostName}'");
                var info = server.RegisterWeb(hostName, item, client);
                if (info == null)
                {
                    await client.webSocket.SendCmdAsync(MessageType.Log, $"  HTTP   | 域名 '{hostName}' 不合法，已跳过该站点", CancellationToken.None);
                    continue;
                }

                await client.webSocket.SendCmdAsync(MessageType.Log, $"  HTTP   | http://{hostName}:{client.ConnectionPort} => {item.LocalIp}:{item.LocalPort}", CancellationToken.None);
                client.AddWeb(info);

                if (item.WWW != null)
                {
                    foreach (var www in item.WWW)
                    {
                        // TODO:validateDomain
                        hostName = www.Trim().ToLower();

                        // 别名域名用独立的 WebInfo，注销时才能逐个从路由表摘干净
                        var alias = server.RegisterWeb(hostName, item, client);
                        if (alias == null)
                        {
                            await client.webSocket.SendCmdAsync(MessageType.Log, $"  HTTP   | 域名 '{hostName}' 不合法，已跳过该别名", CancellationToken.None);
                            continue;
                        }

                        await client.webSocket.SendCmdAsync(MessageType.Log, $"  HTTP   | http://{hostName}:{client.ConnectionPort} => {item.LocalIp}:{item.LocalPort}", CancellationToken.None);
                        client.AddWeb(alias);
                    }
                }
            }
        }

        if (requet.Forwards != null && requet.Forwards.Any())
        {
            if (server.ServerOption.CurrentValue.EnableForward)
            {
                hasTunnel = true;

                foreach (var item in requet.Forwards)
                {
                    try
                    {
                        if (item.LocalPort == 3389)
                            tips.Add("您已将3389端口暴露，请确保您的PC密码足够安全。");

                        if (item.LocalPort == 22)
                            tips.Add("您已将22端口暴露，请确保您的PC密码足够安全。");

                        if (server.ForwardList.TryGetValue(item.RemotePort, out var old))
                        {
                            logger.LogDebug($"Remove Listener {old.Listener.ListenIp}:{old.Listener.ListenPort}");
                            old.Listener.Stop();
                            server.ForwardList.TryRemove(item.RemotePort, out var _);
                        }

                        // 监听端口绑 0.0.0.0：客户端离线时由 TunnelClient 注销流程统一
                        // Stop() 并从 ForwardList 摘掉，不靠这里回收。
                        IPortListener ls = item.Protocol == ProtocolEnum.UDP
                            ? new UdpProxyListener("0.0.0.0", item.RemotePort, logger, client.webSocket)
                            : new PortProxyListener("0.0.0.0", item.RemotePort, logger, client.webSocket);
                        ls.Start(new ForwardDispatcher(logger, server, item));

                        var forwardInfo = new ForwardInfo<ForwardHandlerArg> { Listener = ls, Socket = client.webSocket, SSHConfig = item };

                        server.ForwardList.TryAdd(item.RemotePort, forwardInfo);
                        logger.LogDebug($"SSH proxy success: {item.RemotePort} => {item.LocalIp}:{item.LocalPort}");

                        client.AddForward(forwardInfo);
                        await client.webSocket.SendCmdAsync(MessageType.Log, $"  {item.Protocol}    | {server.ServerOption.CurrentValue.WebDomain}:{item.RemotePort} => {item.LocalIp}:{item.LocalPort}", CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($"SSH proxy error: {item.RemotePort} => {item.LocalIp}:{item.LocalPort}");
                        logger.LogError(ex.Message);
                        await client.webSocket.SendCmdAsync(MessageType.Log, ex.Message, CancellationToken.None);
                        continue;
                    }
                }
            }
            else
            {
                await client.webSocket.SendCmdAsync(MessageType.Log, TunnelResource.ForwardDisabled, CancellationToken.None);
            }
        }

        foreach (var item in tips)
        {
            await client.webSocket.SendCmdAsync(MessageType.Log, item, CancellationToken.None);
        }

        if (!hasTunnel)
        {
            await client.webSocket.SendCmdAsync(MessageType.Log, TunnelResource.NoTunnel, CancellationToken.None);
        }

        client.LoginCompleted = true;
        logger.LogInformation($"[Session {client.SessionId}] 登录完成：HTTP 站点 {client.WebInfos.Count} 个，端口转发 {client.ForwardInfos.Count} 个，"
            + $"当前 WebList {server.WebList.Count}｜ForwardList {server.ForwardList.Count}");
    }


    public virtual async Task<bool> HandlerMsg(FastTunnelServer fastTunnelServer, TunnelClient tunnelClient, string lineCmd, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lineCmd))
        {
            logger.LogWarning($"收到空的控制指令，来源 {tunnelClient.RemoteIpAddress}，已忽略");
            return false;
        }

        var msg = JsonSerializer.Deserialize<LogInMassage>(lineCmd, SourceGenerationContext.Default.LogInMassage);
        if (msg == null)
        {
            logger.LogWarning($"控制指令无法解析为登录信息，来源 {tunnelClient.RemoteIpAddress}，已忽略");
            return false;
        }

        await HandleLoginAsync(fastTunnelServer, tunnelClient, msg, cancellationToken);
        return NeedRecive;
    }
}
