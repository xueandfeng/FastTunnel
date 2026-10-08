// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Core.Exceptions;
using FastTunnel.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Extensions
{
    public static class WebSocketExtensions
    {
        /// <summary>
        /// WebSocket 不允许并发写（否则报 “Cannot write to the output stream concurrently”）。
        /// 控制通道上同时有登录、心跳、Swap/Forward 指令在发，这里按 socket 加锁把发送串行化；
        /// 用 <see cref="ConditionalWeakTable{TKey, TValue}"/> 避免静态字典泄漏已关闭的 socket。
        /// </summary>
        private static readonly ConditionalWeakTable<WebSocket, SemaphoreSlim> sendLocks = new();

        public static async Task SendCmdAsync(this WebSocket socket, MessageType type, string content, CancellationToken cancellationToken)
        {
            if (socket.State != WebSocketState.Open)
            {
                throw new SocketClosedException(socket.State.ToString());
            }

            var sendLock = sendLocks.GetValue(socket, _ => new SemaphoreSlim(1, 1));
            // 只有带超时的重载会返回 Task<bool>；给 30s 排队上限，避开前一次发送被链路卡死
            if (!await sendLock.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken))
            {
                // 排队超时：此时并未持有信号量，不能 Release
                throw new SocketClosedException($"{socket.State}，发送排队超时");
            }
            try
            {
                // 排队期间对端可能已经关闭，再校验一次
                if (socket.State != WebSocketState.Open)
                {
                    throw new SocketClosedException(socket.State.ToString());
                }

                var buffer = Encoding.UTF8.GetBytes($"{(char)type}{content}\n");
                await socket.SendAsync(buffer, WebSocketMessageType.Binary, false, cancellationToken);
            }
            finally
            {
                sendLock.Release();
            }
        }
    }
}
