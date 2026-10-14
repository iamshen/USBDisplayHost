using System.Buffers.Binary;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Themes;

public static class ImageInputInspector
{
    public const int MaximumFileBytes = 20 * 1024 * 1024;
    public const long MaximumPixels = 20_000_000;

    public static OperationResult<ImageInfo> Inspect(Stream source, CancellationToken cancellationToken = default)
    {
        try { return InspectHeader(source, cancellationToken); }
        catch (EndOfStreamException) { return Invalid("图片头部已截断"); }
    }

    private static OperationResult<ImageInfo> InspectHeader(Stream source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.CanSeek || source.Length is < 24 or > MaximumFileBytes)
        {
            return Invalid("图片文件为空、过小或超过 20MiB");
        }

        source.Position = 0;
        Span<byte> header = stackalloc byte[24];
        source.ReadExactly(header);
        if (header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13
                || !header[12..16].SequenceEqual("IHDR"u8))
            {
                return Invalid("PNG 头部无效");
            }
            return Dimensions(BinaryPrimitives.ReadUInt32BigEndian(header[16..20]),
                BinaryPrimitives.ReadUInt32BigEndian(header[20..24]), ImageFileFormat.Png);
        }

        if (header[0] != 0xFF || header[1] != 0xD8)
        {
            return Invalid("仅支持 PNG 或 JPEG 图片");
        }

        source.Position = 2;
        Span<byte> lengthBytes = stackalloc byte[2];
        Span<byte> dimensions = stackalloc byte[5];
        while (source.Position < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.ReadByte() != 0xFF)
            {
                return Invalid("JPEG 标记无效");
            }
            int marker;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                marker = source.ReadByte();
            } while (marker == 0xFF);
            if (marker < 0 || marker is 0x00 or 0xD8 or 0xD9 or 0xDA)
            {
                return Invalid("JPEG 在尺寸信息前已截断或结束");
            }
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            source.ReadExactly(lengthBytes);
            int length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (length < 2 || source.Position + length - 2 > source.Length)
            {
                return Invalid("JPEG 段长度无效");
            }
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
                or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (length < 8)
                {
                    return Invalid("JPEG 尺寸段无效");
                }
                source.ReadExactly(dimensions);
                return Dimensions(BinaryPrimitives.ReadUInt16BigEndian(dimensions[3..5]),
                    BinaryPrimitives.ReadUInt16BigEndian(dimensions[1..3]), ImageFileFormat.Jpeg);
            }
            source.Seek(length - 2, SeekOrigin.Current);
        }
        return Invalid("图片缺少尺寸信息");
    }

    private static OperationResult<ImageInfo> Dimensions(uint width, uint height, ImageFileFormat format) =>
        width == 0 || height == 0 || (ulong)width * height > MaximumPixels
            ? Invalid("图片尺寸无效或超过 2000 万像素")
            : new OperationResult<ImageInfo>.Success(new ImageInfo((int)width, (int)height, format));

    private static OperationResult<ImageInfo> Invalid(string reason) =>
        new OperationResult<ImageInfo>.Failure(AppError.Create("InvalidImage", reason, "请选择有效且未超限的 PNG/JPEG 图片"));
}
