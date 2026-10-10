// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace FastTunnel.Core.Models
{
    public class WebInfo
    {
        public WebSocket Socket { get; set; }

        public WebConfig WebConfig { get; set; }

        /// <summary>
        /// 注册该后端的站点域名，注销时按它从路由表里精确摘除
        /// </summary>
        public string Domain { get; set; } = string.Empty;

        /// <summary>
        /// 注册该后端的客户端会话编号
        /// </summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>
        /// 注册时刻（UTC），选路排序用
        /// </summary>
        public DateTime RegisteredUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 该后端的选路优先级，取自客户端该站点的 WebConfig.Priority（默认 0）。
        /// 只在同一个站点上的多个后端之间比：健康后端里优先级大的先选，相等时回到“后注册者优先”。
        /// </summary>
        public int Priority { get; set; }

        /// <summary>
        /// 该后端所在控制通道是否还能用。已 Dispose 的 WebSocket 读 State 会抛异常，
        /// 这里一律当作“不可用”，不能让一个诊断属性把请求带倒。
        /// </summary>
        public bool IsAlive
        {
            get
            {
                try
                {
                    return Socket != null && Socket.State == WebSocketState.Open;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        private int consecutiveTimeouts;

        /// <summary>
        /// 连续降级次数（隧道握手超时或发送失败），成功一次即清零。
        /// 用于把“我们等急了”和“后端真坏了”区分开：少量降级不摘除，连片降级才判定失活。
        /// </summary>
        public int ConsecutiveTimeouts => Volatile.Read(ref consecutiveTimeouts);

        /// <summary>记一次降级，返回累计次数。</summary>
        public int RegisterTimeout() => Interlocked.Increment(ref consecutiveTimeouts);

        /// <summary>隧道建立成功，清零降级计数。</summary>
        public void ResetTimeouts() => Interlocked.Exchange(ref consecutiveTimeouts, 0);
    }
}
