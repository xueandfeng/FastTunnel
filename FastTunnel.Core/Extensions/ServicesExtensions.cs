// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System.Threading;
using System.Threading.Tasks;
using FastTunnel.Core.Client;
using FastTunnel.Core.Config;
using FastTunnel.Core.Filters;
using FastTunnel.Core.Forwarder;
using FastTunnel.Core.Forwarder.MiddleWare;
using FastTunnel.Core.Handlers.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;

namespace FastTunnel.Core.Extensions;

public static class ServicesExtensions
{

    /// <summary>
    /// 添加服务端后台进程
    /// </summary>
    /// <param name="services"></param>
    public static void AddFastTunnelServer(this IServiceCollection services, IConfigurationSection configurationSection)
    {
        services.AddReverseProxy().LoadFromMemory();
        services.AddSingleton<IForwarderHttpClientFactory, FastTunnelForwarderHttpClientFactory>();
        services.AddHttpContextAccessor();

        services.Configure<DefaultServerConfig>(configurationSection)
            .AddSingleton<IExceptionFilter, FastTunnelExceptionFilter>()
            .AddTransient<ILoginHandler, LoginHandler>()
            .AddSingleton<FastTunnelClientHandler>()
            .AddSingleton<FastTunnelSwapHandler>()
            .AddSingleton<FastTunnelServer>();
    }

    /// <summary>
    /// 服务端中间件
    /// </summary>
    /// <param name="app"></param>
    public static void UseFastTunnelServer(this IApplicationBuilder app)
    {
        app.UseWebSockets();

        var swapHandler = app.ApplicationServices.GetRequiredService<FastTunnelSwapHandler>();
        var clientHandler = app.ApplicationServices.GetRequiredService<FastTunnelClientHandler>();
        app.Use(clientHandler.Handle);
        app.Use(swapHandler.Handle);
    }

    public static void MapFastTunnelServer(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapReverseProxy();
        endpoints.MapFallback(context =>
        {
            var options = context.RequestServices.GetRequiredService<IOptionsMonitor<DefaultServerConfig>>();
            var host = context.Request.Host.Host;
            if (!host.EndsWith(options.CurrentValue.WebDomain) || host.Equals(options.CurrentValue.WebDomain))
            {
                context.Response.StatusCode = 404;
                return Task.CompletedTask;
            }

            // “域名对但站点没注册”同样是失败，不能回 200：否则监控与客户端把“查无此站”当成成功。
            // 这里用 404（资源不存在）而不是 503（服务端暂时不可用），两者在告警里语义不同。
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return context.Response.WriteAsync(TunnelResource.Page_NotFound, CancellationToken.None);
        });
    }
}
