using Microsoft.AspNetCore.Connections.Features;
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace FastTunnel.Core.Forwarder
{
    /// <summary>
    /// 把 Kestrel 这条连接的两个管道包装成 Stream 交给隧道的使用方
    /// （YARP 的 HttpClient、端口转发、UDP 会话）。
    ///
    /// 为什么不直接用 <c>transportFeature.Transport.Input.AsStream()</c>：
    /// 那个 <c>PipeReaderStream</c> 在被 <see cref="PipeReader.CancelPendingRead"/> 唤醒之后会
    /// 【自己再挂一次读】，直到管道被完成为止才松手。而 Kestrel 要等应用的请求处理结束才去
    /// 完成管道——也就是说：只要中间件先返回，Kestrel 就会在同一条连接上发起“读下一个请求”，
    /// 和这个还不肯松手的读撞上，抛
    /// <c>InvalidOperationException: Concurrent reads or writes are not supported.</c>，
    /// 服务端日志里那句 <c>Connection processing ended abnormally.</c> 就是它（本机实测：
    /// 隧道被拆时同一个读点还会以 ConnectionResetException 的形态出现，见日志
    /// Http1Connection.BeginRead → HttpProtocol.ProcessRequests）。
    ///
    /// 所以这里自己拿读循环：关闭之后绝不再挂新的读，并把“读写已全部脱离管道”做成一个
    /// 可等待的信号 <see cref="Released"/>，让中间件能等到这一刻再把连接交回 Kestrel。
    /// </summary>
    sealed class WebSocketStream : Stream
    {
        private readonly PipeReader? input;
        private readonly PipeWriter? output;
        private readonly Stream? passthrough;
        private readonly IConnectionLifetimeFeature? lifetimeFeature;

        /// <summary>进行中的读/写个数。为 0 且已关闭，才代表这条管道不再被我们占着。</summary>
        private int activeOps;

        /// <summary>0 = 可用；1 = 已关闭（不再挂任何读写）。</summary>
        private int closed;

        private readonly TaskCompletionSource releasedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WebSocketStream(IConnectionLifetimeFeature lifetimeFeature, IConnectionTransportFeature transportFeature)
        {
            this.input = transportFeature.Transport.Input;
            this.output = transportFeature.Transport.Output;
            this.lifetimeFeature = lifetimeFeature;
        }

        public WebSocketStream(Stream stream)
        {
            this.passthrough = stream;
            this.lifetimeFeature = null;
        }

        /// <summary>
        /// 读写是否已经彻底脱离这条连接的管道。只有到这里为止，中间件才能安全地把连接交还 Kestrel。
        /// </summary>
        public Task Released => releasedSource.Task;

        /// <summary>
        /// 结束隧道：标记关闭、叫醒挂在管道上的读写、打断这条连接，然后等读写真正脱离。
        /// </summary>
        /// <param name="grace">最多等多久。超时说明使用方还占着管道，调用方应当记一条日志。</param>
        /// <returns>true = 已彻底脱离；false = 到时间了还没脱离。</returns>
        public async Task<bool> ShutdownAsync(TimeSpan grace)
        {
            CloseCore();

            using var cts = new CancellationTokenSource(grace);
            try
            {
                await releasedSource.Task.WaitAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// 只负责“让读写松手”，不等：使用方 Dispose 走这里，避免在同步释放里卡线程。
        /// </summary>
        private void CloseCore()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0)
            {
                try { input?.CancelPendingRead(); } catch (Exception) { }
                try { output?.CancelPendingFlush(); } catch (Exception) { }

                // 连接本身交给 Kestrel 收尾：这一句原有的作用是让隧道两端立刻报错退出
                lifetimeFeature?.Abort();
            }

            TryCompleteReleased();
        }

        private void TryCompleteReleased()
        {
            if (Volatile.Read(ref closed) != 0 && Volatile.Read(ref activeOps) == 0)
            {
                releasedSource.TrySetResult();
            }
        }

        private void BeginOp() => Interlocked.Increment(ref activeOps);

        private void EndOp()
        {
            if (Interlocked.Decrement(ref activeOps) == 0)
            {
                TryCompleteReleased();
            }
        }

        private bool IsClosed => Volatile.Read(ref closed) != 0;

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
            FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (passthrough != null)
            {
                await passthrough.FlushAsync(cancellationToken);
                return;
            }

            if (IsClosed)
            {
                throw new IOException("隧道已关闭，无法写出。");
            }

            // 挂着的 flush 也算“还占着这条管道”，计入活动操作数，否则中间件会以为已经安全脱手
            BeginOp();
            try
            {
                var flushed = await output!.FlushAsync(cancellationToken);
                if (flushed.IsCanceled || IsClosed)
                {
                    throw new IOException("隧道已关闭，数据未能写完。");
                }
            }
            finally
            {
                EndOp();
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(new Memory<byte>(buffer, offset, count)).AsTask().GetAwaiter().GetResult();

        public override int Read(Span<byte> buffer)
        {
            // 同步读只可能来自旧的同步使用方：借一个临时数组走异步实现，然后阻塞等结果
            var temp = new byte[buffer.Length];
            var read = ReadAsync(new Memory<byte>(temp)).AsTask().GetAwaiter().GetResult();
            temp.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (passthrough != null)
            {
                return await passthrough.ReadAsync(buffer, cancellationToken);
            }

            if (buffer.Length == 0)
            {
                return 0;
            }

            BeginOp();
            try
            {
                while (true)
                {
                    if (IsClosed || cancellationToken.IsCancellationRequested)
                    {
                        // 关键：关闭之后绝不再挂新的读，读取权立刻还给 Kestrel
                        return 0;
                    }

                    ReadResult result;
                    try
                    {
                        result = await input!.ReadAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return 0;
                    }

                    var start = result.Buffer.Start;
                    var end = result.Buffer.End;

                    if (result.IsCanceled)
                    {
                        // 单参数 AdvanceTo：examined 跟着 consumed 走。被叫醒的这一次可能缓冲区里还有
                        // 没取走的数据，若把 examined 推到末尾，管道会认为“数据都看过”，下一次读要等
                        // 新数据到达才完成——数据就卡在缓冲区里出不去。
                        input.AdvanceTo(start);
                        if (IsClosed)
                        {
                            return 0;
                        }
                        continue;
                    }

                    var available = result.Buffer.Length;
                    if (available == 0)
                    {
                        input.AdvanceTo(start, end);
                        if (result.IsCompleted)
                        {
                            // 管道被完成且没有残余数据：这才是真的 EOF
                            return 0;
                        }
                        // 还没结束却读到空缓冲区：继续等，绝不能当成 EOF 提前交给使用方
                        continue;
                    }

                    var take = available <= buffer.Length ? (int)available : buffer.Length;
                    result.Buffer.Slice(0, take).CopyTo(buffer.Span);
                    var consumed = result.Buffer.GetPosition(take);
                    if (take == available)
                    {
                        input.AdvanceTo(consumed, end);
                    }
                    else
                    {
                        // 只取走了一部分（使用方给的缓冲区比缓冲区小）：剩下的保持“未检视”，
                        // 这样使用方下一次读能立刻拿到，不必等对端再发数据。见上面的单参数说明。
                        input.AdvanceTo(consumed);
                    }
                    return take;
                }
            }
            finally
            {
                EndOp();
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteAsync(new ReadOnlyMemory<byte>(buffer.ToArray())).AsTask().GetAwaiter().GetResult();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (passthrough != null)
            {
                await passthrough.WriteAsync(buffer, cancellationToken);
                return;
            }

            if (buffer.Length == 0)
            {
                return;
            }

            BeginOp();
            try
            {
                if (IsClosed)
                {
                    throw new IOException("隧道已关闭，无法写出。");
                }

                while (!buffer.IsEmpty)
                {
                    var memory = output!.GetMemory(buffer.Length);
                    if (memory.Length == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        continue;
                    }

                    var take = memory.Length <= buffer.Length ? memory.Length : buffer.Length;
                    buffer.Span.Slice(0, take).CopyTo(memory.Span);
                    output.Advance(take);
                    buffer = buffer.Slice(take);
                }

                var flushed = await output!.FlushAsync(cancellationToken);
                if (flushed.IsCanceled || IsClosed)
                {
                    throw new IOException("隧道已关闭，数据未能写完。");
                }
            }
            finally
            {
                EndOp();
            }
        }

        protected override void Dispose(bool disposing)
        {
            CloseCore();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            CloseCore();
            return ValueTask.CompletedTask;
        }
    }
}
