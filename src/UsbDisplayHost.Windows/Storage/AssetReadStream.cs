using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Windows.Storage;

// 素材流关闭完成后才释放读取租约；关闭抛出异常时保留引用，不授权素材清理。
internal sealed class AssetReadStream(Stream source, AssetReferenceLease lease) : Stream
{
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => source.Length;
    public override long Position { get => source.Position; set => source.Position = value; }
    public override void Flush() => source.Flush();
    public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => source.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        source.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException("Asset streams are read-only.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Asset streams are read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            source.Dispose();
            lease.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await source.DisposeAsync().ConfigureAwait(false);
        lease.Dispose();
        GC.SuppressFinalize(this);
    }
}
