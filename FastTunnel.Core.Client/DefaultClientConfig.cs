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
        /// 连接前是否先 ICMP ping 一次服务端（仅排查网络用，会拖慢重连）。默认 false。
        /// </summary>
        public bool PingBeforeConnect { get; set; }
    }
}
