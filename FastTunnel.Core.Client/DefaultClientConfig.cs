// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Client;
using FastTunnel.Core.Models;
using System.Collections.Generic;

namespace FastTunnel.Core.Config
{
    public class DefaultClientConfig : IClientConfig
    {
        public DefaultClientConfig() { }
        public SuiDaoServer Server { get; set; }

        public string Token { get; set; }

        public IEnumerable<WebConfig> Webs { get; set; }

        public IEnumerable<ForwardConfig> Forwards { get; set; }

        /// <summary>
        /// 应用层心跳间隔（秒），0 = 关闭。心跳用于刷新中间设备(NAT/防火墙)的会话，
        /// 并在链路半开时主动判定失效并重连。默认 30 秒。
        /// </summary>
        public int HeartbeatSeconds { get; set; } = 30;

        /// <summary>
        /// WebSocket 控制帧 ping 间隔（秒），0 = 用 .NET 默认值(30 秒)。默认 15 秒。
        /// </summary>
        public int WsKeepAliveSeconds { get; set; } = 15;

        /// <summary>
        /// 断线后首次重连的基础间隔（秒），之后按指数退避。默认 5 秒。
        /// </summary>
        public int ReconnectDelaySeconds { get; set; } = 5;

        /// <summary>
        /// 重连指数退避的上限（秒）。默认 60 秒。
        /// </summary>
        public int MaxReconnectDelaySeconds { get; set; } = 60;

        /// <summary>
        /// 退避等待期间探测服务端端口的间隔（秒），0 = 关闭探活。默认 2 秒。
        /// 只在「这一轮压根没连上服务端」时生效：等待期间每隔这么久做一次纯 TCP 连接探测，
        /// 服务端一恢复就立刻打断剩余退避发起重连，不必干等到退避上限。
        /// </summary>
        public int ReconnectProbeIntervalSeconds { get; set; } = 2;

        /// <summary>
        /// 会话主动轮换间隔（分钟），0 = 不轮换（默认）。
        /// 开启后客户端每活过这么久就主动发 Close 帧重连一次。它用来躲中间设备的
        /// “最长会话时长”硬上限（常见 7200 秒 = 2 小时）：那种上限不按流量重置，
        /// 心跳调多密都挨同一刀；只能自己先到点把连接换掉，并把阈值配到上限之前留余量。
        /// 多台客户端挂同一站点时不必人工错开取值，交给 <see cref="SessionRotateJitterSeconds"/>。
        /// </summary>
        public int MaxSessionMinutes { get; set; }

        /// <summary>
        /// 会话轮换的随机抖动上限（秒），0 = 不抖动。默认 0。
        /// 每轮会话的实际轮换时刻 = MaxSessionMinutes 再加上 0~这么多个秒数的随机偏移（按秒取随机，
        /// 不是按分钟，否则几台仍会撞在同一分钟上）。目的是让多台客户端的轮换时刻错开：
        /// 若几台在同一刻一起换连接，会出现所有后端同时为空、站点短暂返回离线页。
        /// 只在 MaxSessionMinutes &gt; 0 时生效；取值要留在设备上限之前的余量之内。
        /// 代价是每台每次的断开时刻不可预测，看日志时以“会话轮换已排期”那条给的实际时刻为准。
        /// </summary>
        public int SessionRotateJitterSeconds { get; set; }

        /// <summary>
        /// 连接前是否先 ICMP ping 一次服务端（仅排查网络用，会拖慢重连）。默认 false。
        /// </summary>
        public bool PingBeforeConnect { get; set; }
    }
}
