using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CloudStore.Core;

/// <summary>
/// 包一层流，在读取时累计字节数用于进度上报。
///
/// 注意：必须保持可定位（CanSeek / Seek / Position 全部透传）。
/// AWS SDK 会先顺序读一遍载荷算 payload SHA256，再回退到 0 真正发送；
/// 一旦这里报 CanSeek=false，SDK 就无法计算哈希，会退化成分块流式签名
/// （x-amz-content-sha256: STREAMING-AWS4-HMAC-SHA256-PAYLOAD），
/// 而 Cloudflare R2 不实现该签名，所有 PUT / UploadPart 都会被直接拒绝。
/// </summary>
internal sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _onRead;
    private long _count;
    private long _reported;

    public ProgressStream(Stream inner, Action<long> onRead)
    {
        _inner = inner;
        _onRead = onRead;
    }

    /// <summary>已累计读取的字节数（用于补报位置，不受回退影响）。</summary>
    public long TotalRead => Interlocked.Read(ref _count);

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);

        if (read > 0)
        {
            Report();
        }

        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return ReadCoreAsync(buffer, cancellationToken);
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (read > 0)
        {
            Report();
        }

        return read;
    }

    private void Report()
    {
        if (_inner.CanSeek)
        {
            long position = _inner.Position;

            if (position < _reported)
            {
                // 回退后重新计，避免把 SDK 的预读算进真实进度
                _reported = position;
                return;
            }

            long delta = position - _reported;
            _reported = position;

            if (delta > 0)
            {
                Interlocked.Add(ref _count, delta);
                _onRead(delta);
            }

            return;
        }

        Interlocked.Add(ref _count, 1);
        _onRead(1);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long position = _inner.Seek(offset, origin);

        // SDK 算完哈希会回退到起点重新发送，这里重置进度基准。
        if (position < _reported)
        {
            _reported = position;
        }

        return position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
