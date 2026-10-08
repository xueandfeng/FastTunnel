// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Client;
using FastTunnel.Server;
using FastTunnel.Server.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FastTunnel.Api.Controllers;

public class SystemController : BaseController
{
    readonly FastTunnelServer fastTunnelServer;

    public SystemController(FastTunnelServer fastTunnelServer)
    {
        this.fastTunnelServer = fastTunnelServer;
    }

    /// <summary>
    /// 获取当前等待响应的请求
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    public ApiResponse GetResponseTempList()
    {
        ApiResponse.data = new
        {
            Count = fastTunnelServer.ResponseTasks.Count,
            Rows = fastTunnelServer.ResponseTasks.Select(x => new
            {
                x.Key
            })
        };

        return ApiResponse;
    }

    /// <summary>
    /// 获取当前映射的所有站点信息
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    public ApiResponse GetAllWebList()
    {
        ApiResponse.data = new
        {
            Count = fastTunnelServer.WebList.Count,
            Rows = fastTunnelServer.WebList.Select(x => new
            {
                x.Key,
                LocalIp = x.Value.First()?.WebConfig?.LocalIp,
                LocalPort = x.Value.First()?.WebConfig?.LocalPort,
                Backends = x.Value.Count,
                Alive = x.Value.Snapshot().Count(w => w.IsAlive),
                Degraded = x.Value.Snapshot().Count(w => w.ConsecutiveTimeouts > 0)
            })
        };

        return ApiResponse;
    }

    /// <summary>
    /// 获取服务端配置信息（只输出非敏感字段）。
    ///
    /// 这里以前是 ApiResponse.data = fastTunnelServer.ServerOption;——直接把配置对象交给序列化，
    /// 而 DefaultServerConfig 上的 Tokens（客户端登录凭证）、Api.JWT.IssuerSigningKey（签名密钥）、
    /// Api.Accounts[].Password（管理端口令）全是普通自动属性，一个 [JsonIgnore] 都没加，
    /// 等于一个读接口把全部凭证送出去。而 GetToken 是 [AllowAnonymous]，只要仓库默认口令
    /// admin/admin123 没改，任何人两步就能拿到：匿名换 JWT → 读全部 Token → 挂伪客户端接管站点流量。
    /// 所以改成白名单投影：新增配置字段时默认不对外，需要露出去要显式加一行。
    ///
    /// 另外那个对象本身是 IOptionsMonitor&lt;DefaultServerConfig&gt;，把它当响应体序列化本来就是错的。
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    public ApiResponse GetServerOption()
    {
        var option = fastTunnelServer.ServerOption.CurrentValue;

        ApiResponse.data = new
        {
            option.WebDomain,
            option.EnableForward,
            option.ClientIdleTimeoutSeconds,
            option.DiagnosticsLogIntervalSeconds,
            option.WebBackendWaitSeconds,
            option.WebBackendTimeoutTripCount,
            option.WebReuseTunnel,
            option.WebResponseTimeoutSeconds,
            option.WebMaxRequestBodySizeMb,

            // 下面三项只给数量不给内容：运维需要能判断“Token 到底配了没、客户端为什么被拒”，
            // 但不需要服务端把 Token 明文回给调用方。
            TokenCount = option.Tokens?.Count ?? 0,
            AccountCount = option.Api?.Accounts?.Length ?? 0,
            Jwt = new
            {
                option.Api?.JWT?.ValidIssuer,
                option.Api?.JWT?.ValidAudience,
                option.Api?.JWT?.ClockSkew,
                option.Api?.JWT?.Expires,
                // 只回答“密钥是不是还在用仓库默认值”，不回密钥本体
                IssuerSigningKeyIsDefault = StartupSafetyCheck.IsDefaultSigningKey(option.Api?.JWT?.IssuerSigningKey)
            }
        };

        return ApiResponse;
    }

    /// <summary>
    /// 获取所有端口转发映射列表
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    public ApiResponse GetAllForwardList()
    {
        ApiResponse.data = new
        {
            Count = fastTunnelServer.ForwardList.Count,
            Rows = fastTunnelServer.ForwardList.Select(x => new
            {
                x.Key,
                x.Value.SSHConfig.LocalIp,
                x.Value.SSHConfig.LocalPort,
                x.Value.SSHConfig.RemotePort
            })
        };

        return ApiResponse;
    }

    /// <summary>
    /// 获取当前客户端在线数量
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    public ApiResponse GetOnlineClientCount()
    {
        ApiResponse.data = fastTunnelServer.ConnectedClientCount;
        return ApiResponse;
    }

    [HttpGet]
    public ApiResponse Clients()
    {
        ApiResponse.data = fastTunnelServer.Clients.Select(x => new
        {
            x.WebInfos,
            x.ForwardInfos,
            RemoteIpAddress = x.RemoteIpAddress.ToString(),
            StartTime = x.StartTime.ToString("yyyy-MM-dd HH:mm:ss")
        });
        return ApiResponse;
    }
}
