// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FastTunnel.Core.Utilitys
{


    public class WebSocketUtility
    {
        /// <summary>EndReason 的初始值，表示读循环还在跑。</summary>
        private const string RunningReason = "running";

        private readonly WebSocket webSocket;

        public WebSocketUtility(WebSocket webSocket, Action<ReadOnlySequence<byte>, CancellationToken> processLine)
        {
            this.webSocket = webSocket;
            ProcessLine = processLine;
        }

        public Action<ReadOnlySequence<byte>, CancellationToken> ProcessLine { get; }

        /// <summary>
        /// 读循环退出的原因，用于定位“不定时重连”到底是谁先断的。
        /// </summary>
        public string EndReason { get; private set; } = RunningReason;

        /// <summary>
        /// 读循环收到的未预期异常（网络错误、RST 等），无异常时为 null。
        /// </summary>
        public Exception? EndException { get; private set; }

        /// <summary>
        /// 最近一次收到数据帧的时刻（UTC）。WebSocket 的 ping/pong 控制帧不会上报到这里。
        /// </summary>
        public DateTime LastDataUtc { get; private set; } = DateTime.UtcNow;

        public async Task ProcessLinesAsync(CancellationToken cancellationToken)
        {
            var pipe = new Pipe();
            var writing = FillPipeAsync(webSocket, pipe.Writer, cancellationToken);
            var reading = ReadPipeAsync(pipe.Reader, cancellationToken);

            try
            {
                await Task.WhenAll(reading, writing);
            }
            catch (OperationCanceledException)
            {
                // 取消只可能来自调用方的令牌：服务端传的是 context.RequestAborted（对端拆链与
                // 本进程停机都会触发它，两者在这里分不开，要靠调用方另判），客户端传的是宿主停机令牌。
                //
                // 这个异常以前会一路逃出 WhenAll —— ReadPipeAsync 里的 reader.ReadAsync(ct)
                // 与 FillPipeAsync 里的 writer.FlushAsync(ct) 都没包 try。调用方写在
                // ProcessLinesAsync 之后的会话摘要因此整条没机会执行，而最需要
                // “退出原因 / 距上次入站”这两个字段的，恰恰是被外部拆链的那种场合。
                // 现场表现就是服务端只剩一句“控制通道异常终止：OperationCanceledException”。
                if (EndReason == RunningReason)
                {
                    EndReason = "读循环被取消(调用方令牌触发)";
                }
            }
        }

        /// <summary>
        /// 读取socket收到的消息写入Pipe
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="writer"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        private async Task FillPipeAsync(WebSocket socket, PipeWriter writer, CancellationToken cancellationToken)
        {
            const int minimumBufferSize = 512;

            try
            {
                while (true)
                {
                    // Allocate at least 512 bytes from the PipeWriter.
                    var memory = writer.GetMemory(minimumBufferSize);

                    try
                    {
                        var bytesRead = await socket.ReceiveAsync(memory, cancellationToken);
                        if (bytesRead.MessageType == WebSocketMessageType.Close)
                        {
                            EndReason = $"对端发送 Close 帧(closeStatus={socket.CloseStatus}, description={socket.CloseStatusDescription})";
                            break;
                        }
                        if (bytesRead.Count == 0)
                        {
                            EndReason = "读到 0 字节(流结束)";
                            break;
                        }
                        if (bytesRead.EndOfMessage)
                        {
                            // 注意：本项目的控制协议靠 “发送端永远 endOfMessage:false” 分帧，
                            // 一旦对端以完整消息发送，这里就会被当成连接结束。
                            EndReason = "收到 EndOfMessage=true 的完整消息(对端分帧方式与本端预期不一致)";
                            break;
                        }

                        LastDataUtc = DateTime.UtcNow;
                        // Tell the PipeWriter how much was read from the Socket.
                        writer.Advance(bytesRead.Count);
                    }
                    catch (OperationCanceledException)
                    {
                        // 不要写成“正在停止”：服务端这条令牌在对端拆链时也会触发，
                        // 按“停止”解读会把链路故障读成本机停机，排障方向整个反掉。
                        EndReason = "读被取消(调用方令牌触发：对端拆链或本机停机)";
                        break;
                    }
                    catch (Exception ex)
                    {
                        EndReason = $"读取异常 {ex.GetType().Name}: {ex.Message}";
                        EndException = ex;
                        break;
                    }

                    // Make the data available to the PipeReader.
                    var result = await writer.FlushAsync(cancellationToken);

                    if (result.IsCompleted)
                    {
                        EndReason = "管道已关闭";
                        break;
                    }
                }
            }
            finally
            {
                // By completing PipeWriter, tell the PipeReader that there's no more data coming.
                await writer.CompleteAsync();
            }
        }

        /// <summary>
        /// 从Pipe中读取收到的消息
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task ReadPipeAsync(PipeReader reader, CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    var result = await reader.ReadAsync(cancellationToken);
                    var buffer = result.Buffer;

                    while (TryReadLine(ref buffer, out ReadOnlySequence<byte> line))
                    {
                        // Process the line.
                        ProcessLine(line, cancellationToken);
                    }

                    // Tell the PipeReader how much of the buffer has been consumed.
                    reader.AdvanceTo(buffer.Start, buffer.End);

                    // Stop reading if there's no more data coming.
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            finally
            {
                // Mark the PipeReader as complete.
                // 放在 finally 里：以前被取消时这一句会被跳过，Pipe 另一端的 writer 就得干等，
                // 两条支路不能一起收尾。取消本身不是错误，不该让清理也跟着一起被跳过。
                try
                {
                    await reader.CompleteAsync();
                }
                catch (Exception)
                {
                    // 重复 Complete 在 Pipe 已结束时不需要再处理，这里不能完全没兜底
                }
            }
        }

        bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
        {
            // Look for a EOL in the buffer.
            SequencePosition? position = buffer.PositionOf((byte)'\n');

            if (position == null)
            {
                line = default;
                return false;
            }

            // Skip the line + the \n.
            line = buffer.Slice(0, position.Value);
            buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
            return true;
        }
    }
}
