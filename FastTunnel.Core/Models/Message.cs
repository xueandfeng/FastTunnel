// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Collections.Generic;
using System.Text;

namespace FastTunnel.Core.Models
{
    public struct Message<T>
    {
        public MessageType MessageType { get; set; }

        public T Content { get; set; }
    }

    public enum MessageType : byte
    {
        LogIn = 1, // client
        SwapMsg = 2,
        Forward = 3,
        Log = 4,
        /// <summary>
        /// 应用层心跳。客户端周期性发送，服务端原样回显负载用于计算RTT；
        /// 老服务端不认识该命令时只会打印一条“处理客户端消息失败”，不会断开连接，
        /// 客户端检测到无回显会自动降级（见 FastTunnelClient）。
        /// </summary>
        HeartBeat = 5,
    }

    public static class Heartbeat
    {
        /// <summary>心跳负载前缀，后跟发送时的 <see cref="DateTime.Ticks"/>，用于回显算RTT。</summary>
        public const string PayloadPrefix = "hb ";
    }
}
