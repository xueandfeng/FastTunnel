// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using FastTunnel.Api.Filters;
using FastTunnel.Core.Config;
using FastTunnel.Core.Extensions;
using FastTunnel.Server.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FastTunnel.Server;

public class Program
{
    [RequiresUnreferencedCode("")]
    public static void Main(string[] args)
    {
        // 启动引导日志：与正式日志同个 logs/ 目录（容器里该目录已挂到宿主机），
        // 留下配置错误、端口被占这类“启动即失败”的现场。
        Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.File(
                    "logs/fasttunnel-server-boot-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = args
            });

            // Add services to the container.
            builder.Services.AddSingleton<CustomExceptionFilterAttribute>();
            builder.Services.AddControllers();
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("corsPolicy", policy =>
                {
                    policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()
                        .WithExposedHeaders("Set-Token");
                });
            });

            builder.Host.UseSerilog((context, services, configuration) => configuration
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .Filter.ByExcluding(IsTunnelAbortNoise)
                    .WriteTo.Console());

            (builder.Configuration as IConfigurationBuilder).AddJsonFile("config/appsettings.json", optional: false, reloadOnChange: true);
            (builder.Configuration as IConfigurationBuilder).AddJsonFile($"config/appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

            // -------------------FastTunnel STEP1 OF 2------------------
            builder.Services.AddFastTunnelServer(builder.Configuration.GetSection("FastTunnel"));
            // -------------------FastTunnel STEP1 END-------------------

            builder.Services.AddOpenApi();

            var Configuration = builder.Configuration;
            var apioptions = Configuration.GetSection("FastTunnel").Get<DefaultServerConfig>();

            // 访客上传上限。不配时沿用 Kestrel 默认的 30,000,000 字节（约 28.6 MiB），
            // 超过就在服务端直接 413，请求根本进不了隧道（本机实测 28 MB 过、29 MB 413）。
            if (apioptions.WebMaxRequestBodySizeMb > 0)
            {
                var maxRequestBodySize = (long)apioptions.WebMaxRequestBodySizeMb * 1024 * 1024;
                builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodySize);
                Log.Information("访客请求体上限已放宽到 {MaxRequestBodySizeMb} MB（不配时沿用约 28.6 MB）", apioptions.WebMaxRequestBodySizeMb);
            }

            // 响应静默超时没有“更外层的兜底”：客户端隧道两端那个写死的 10 分钟 ReadTimeout
            // 实测在异步转发路径上不生效（见 SwapHandler 的注释），所以调大它就是真等那么久。

            builder.Services.AddAuthentication("Bearer").AddJwtBearer(delegate (JwtBearerOptions options)
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(apioptions.Api.JWT.ClockSkew),
                    ValidateIssuerSigningKey = true,
                    ValidAudience = apioptions.Api.JWT.ValidAudience,
                    ValidIssuer = apioptions.Api.JWT.ValidIssuer,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(apioptions.Api.JWT.IssuerSigningKey))
                };
                options.Events = new JwtBearerEvents
                {
                    OnChallenge = async delegate (JwtBearerChallengeContext context)
                    {
                        // HandleResponse() 之后的响应体由这里写，状态码也得上这里设：
                        // 不写这一行的实测结果是未登录访问拿到 HTTP 200（body 里才是 code:-1 未登录），
                        // 监控、前端 fetch、前置 Nginx 的 4xx 告警全都会把它当成成功。
                        context.HandleResponse();
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.ContentType = "application/json;charset=utf-8";
                        await context.Response.WriteAsJsonAsync(new
                        {
                            code = -1,
                            message = context.Error ?? "未登录"
                        });
                    }
                };
            });

            builder.Host.UseWindowsService();

            var app = builder.Build();

            // 凭证自检：Tokens / JWT 签名密钥 / 管理端口令还是不是仓库默认值或模板占位符，
            // 以及有没有配 WebAllowAccessIps 这个当前没实现的假开关。
            // 只打 Warning 不阻断启动（升级即停服风险更大），但日志里绝对跑不掉；
            // 判定规则与改成硬阻断的方法见 StartupSafetyCheck。
            StartupSafetyCheck.Log(app.Logger, apioptions);

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }

            app.UseCors("corsPolicy");
            app.UseHttpsRedirection();

            app.UseStaticFiles();
            app.UseAuthentication();

            app.UseAuthorization();

            app.MapControllers();

            // -------------------FastTunnel STEP2 OF 2------------------
            app.UseFastTunnelServer();
            app.MapFastTunnelServer();
            // -------------------FastTunnel STEP2 END-------------------

            app.Run();
        }
        catch (System.Exception ex)
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

    /// <summary>
    /// 隧道被响应超时掐断时，YARP 会把 IConnectionLifetimeFeature.Abort() 造成的
    /// ConnectionAbortedException 连带十几行管道堆栈打成 WRN。这是预期路径不是故障：
    /// 堆栈定位不了任何东西，而请求本身已由 FastTunnelSwapHandler 的 [5xx] 一行日志带上
    /// URL 与耗时。这里只抹掉“Yarp + 连接被 abort”这一种组合，其他异常照旧全栈输出。
    /// </summary>
    private static bool IsTunnelAbortNoise(LogEvent logEvent)
    {
        if (logEvent.Exception is not ConnectionAbortedException)
        {
            return false;
        }

        return logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var sourceContext)
            && sourceContext.ToString().Contains("Yarp", StringComparison.OrdinalIgnoreCase);
    }
}
