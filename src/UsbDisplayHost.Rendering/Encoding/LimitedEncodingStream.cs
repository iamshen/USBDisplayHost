using System.IO;

namespace UsbDisplayHost.Rendering.Encoding;

internal sealed class LimitedEncodingStream(int maximumBytes, CancellationToken cancellationToken) : Stream
{
    private readonly MemoryStream buffer = new(Math.Min(maximumBytes, 64 * 1024));
    public bool HasExceededLimit { get; private set; }
    public byte[] ToArray() => buffer.ToArray();
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => buffer.Length;
    public override long Position { get => buffer.Position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() => buffer.Flush();
    public override int Read(byte[] destination, int offset, int count) => buffer.Read(destination, offset, count);

    public override long Seek(long offset, SeekOrigin origin)
    {
        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(buffer.Position + offset),
            SeekOrigin.End => checked(buffer.Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        CheckSize(position);
        return buffer.Seek(position, SeekOrigin.Begin);
    }

    public override void SetLength(long value)
    {
        CheckSize(value);
        buffer.SetLength(value);
    }

    public override void Write(byte[] source, int offset, int count) => Write(source.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> source)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckSize(checked(buffer.Position + source.Length));
        buffer.Write(source);
    }

    private void CheckSize(long size)
    {
        if (size < 0) { throw new IOException("Negative encoding stream position."); }
        if (size > maximumBytes)
        {
            HasExceededLimit = true;
            throw new IOException("Encoded frame size limit exceeded.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { buffer.Dispose(); }
        base.Dispose(disposing);
    }
}
