// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Services
{
    /// <summary>
    /// 客户端宿主服务：把重连循环放到后台跑，停机时先等循环退出、再让客户端优雅下线。
    /// </summary>
    public class ServiceFastTunnelClient : IHostedService
    {
        readonly ILogger<ServiceFastTunnelClient> _logger;
        readonly IFastTunnelClient _fastTunnelClient;
        readonly IHostApplicationLifetime _lifetime;

        /// <summary>后台跑着的重连循环，停机时要等它退出（还没启动过则为 null）。</summary>
        private Task? runLoop;

        public ServiceFastTunnelClient(
            ILogger<ServiceFastTunnelClient> logger,
            IFastTunnelClient fastTunnelClient,
            IHostApplicationLifetime lifetime)
        {
            _logger = logger;
            _fastTunnelClient = fastTunnelClient;
            _lifetime = lifetime;

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        /// <summary>
        /// 这里绝不能 await 重连循环：循环是设计来一直跑到进程停止的，直接 await 就把 Host
        /// 的启动阶段卡死了 —— <c>Run()</c> 永远走不到“等关闭信号”那一步，SIGTERM 没人接，
        /// <c>docker stop</c> 只能等 grace period 到点（默认 10 秒）后 SIGKILL 硬杀，
        /// <see cref="StopAsync"/> 一次都不会执行。那样服务端就只剩“靠 TCP 拆链报文知道客户端走了”
        /// 这一条路，而这个报文经常被中间 NAT/防火墙吞掉，表现就是客户端早就停了、
        /// 服务端 [Snap] 里还挂着僵尸会话和它注册的那批站点后端。
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            // 取消源用 ApplicationStopping，不用传进来的 cancellationToken：
            // 那个 token 属于“启动”阶段（Host 给它配了 StartupTimeout，默认 5 分钟），
            // 长期运行的循环绑上去会在启动 5 分钟后被无故取消。
            // 停机顺序也正好对上：Host 先触发 ApplicationStopping，再逐个调 IHostedService.StopAsync。
            var token = _lifetime.ApplicationStopping;
            // CA2016：这里的第二个参数是“任务启动前就取消”的令牌，不是循环自己的取消源；
            // 循环已经拿着 token 了，所以显式写 None，避开把启动令牌误传进来。
            runLoop = Task.Run(() => RunLoopAsync(token), CancellationToken.None);

            return Task.CompletedTask;
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            try
            {
                await _fastTunnelClient.StartAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 正常停机，不算故障
            }
            catch (Exception ex)
            {
                // 循环内部本来就会自己重试，跑到这里说明是意外。这里必须兜住：
                // 放后台之后再无人 await 这个 Task，不兜住异常就只是静静消失在进程里，
                // 现场看到的是“客户端还活着但再也不连服务端”。
                _logger.LogError(ex, "[Control] 重连循环异常退出，本进程不会再自动连接服务端");
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("===== FastTunnel Client Stoping =====");

            var loop = runLoop;
            if (loop != null)
            {
                // 只给 3 秒：Host 的 ShutdownTimeout 默认才 5 秒，预算花光了就由不得我们；
                // 又不能完全不等 —— 下面的 Close 帧要趁读循环已经退出、socket 还没被 Dispose
                // 的时候发出去，才谈得上“服务端当场收到”。
                var finished = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None));
                if (finished != loop)
                {
                    _logger.LogWarning("[Control] 等待重连循环退出超时(3s)，直接下线");
                }
            }

            await _fastTunnelClient.StopAsync(cancellationToken);
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                _logger.LogError("【UnhandledException】" + e.ExceptionObject);
                var type = e.ExceptionObject.GetType();
                _logger.LogError("ExceptionObject GetType " + type);
            }
            catch
            {
            }
        }
    }
}
