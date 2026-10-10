// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using Microsoft.Extensions.Configuration;
using Serilog;
using FastTunnel.Core.Client.Extensions;
using Serilog.Events;
using Microsoft.Extensions.DependencyInjection;
using FastTunnel.Core.Config;

namespace FastTunnel.Client;

class Program
{
    public static void Main(string[] args)
    {
        // 启动引导日志：写进与正式日志同一个 logs/ 目录（容器里该目录建议挂到宿主机），
        // 这样配置错误、启动即崩溃这类“连不上但没任何日志”的情况也能被留下来。
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                "logs/fasttunnel-client-boot-.log",
                rollingInterval: RollingInterval.Day,
                // 这里只管启动引导日志（JSON 里的 Serilog 节管不到它）；天数要跟
                // appsettings.json 的 retainedFileCountLimit 保持一致，否则两类日志清理节奏不同。
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateBootstrapLogger();

        try
        {
            CreateHostBuilder(args).Build().Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "致命异常");
            throw;
        }
        finally
        {
            // 没有这一句，崩溃前最后一批日志可能还在缓冲区里没落盘
            Log.CloseAndFlush();
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog((context, services, loggerConfiguration) =>
            {
                loggerConfiguration.ReadFrom.Configuration(context.Configuration)
                  .ReadFrom.Services(services)
                  .Enrich.FromLogContext()
                  .WriteTo.Console();
            })
            .UseWindowsService()
            .ConfigureServices((hostContext, services) =>
            {
                // -------------------FastTunnel START------------------
                services.AddFastTunnelClient(hostContext.Configuration.GetSection("FastTunnel"));
                // -------------------FastTunnel EDN--------------------
            });
}
