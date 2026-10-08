// Copyright (c) 2019-2022 Gui.H. https://github.com/FastTunnel/FastTunnel
// The FastTunnel licenses this file to you under the Apache License Version 2.0.
// For more details,You may obtain License file at: https://github.com/FastTunnel/FastTunnel/blob/v2/LICENSE

using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder
{
    /// <summary>
    /// 把访客请求体与响应体拼成一条双向流的 Stream 包装。
    /// 当前转发链路用的是 WebSocketStream / ResponseStream，本类型在仓库内没有任何引用点；
    /// 保留只因为它是 FastTunnel.Core 的公开类型（可能有人嵌用），下个主版本可删除。
    /// 原文件里有三处无条件 Console.WriteLine（ReadAsync 结束 / Dispose / Close）——
    /// 那是排障残留：Console 是同步写，一旦被挂上热路径就是每请求几行标准输出，
    /// 与交付配置把 Yarp.ReverseProxy 降到 Warning 的初衷相反，现已去掉。
    /// </summary>
    public class TranStream : Stream
    {
        private readonly Stream readStream;
        private readonly Stream wirteStream;

        public TranStream(HttpContext context)
        {
            this.readStream = context.Request.BodyReader.AsStream();
            this.wirteStream = context.Response.BodyWriter.AsStream();
        }
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            this.wirteStream.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return this.wirteStream.FlushAsync(cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return this.readStream.Read(buffer, offset, count);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            this.wirteStream.Write(buffer, offset, count);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return this.readStream.ReadAsync(buffer, cancellationToken);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return await this.readStream.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            this.wirteStream.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return this.wirteStream.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await this.wirteStream.WriteAsync(buffer, cancellationToken);
        }
    }
}
