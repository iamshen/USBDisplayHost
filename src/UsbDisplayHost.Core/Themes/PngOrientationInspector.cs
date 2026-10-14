using System.Buffers.Binary;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Themes;

// 只读取 eXIf 的主 IFD 方向，不读取缩略图、GPS 或扩展 IFD。
public static class PngOrientationInspector
{
    private static readonly uint[] CrcTable = CreateCrcTable();
    public static OperationResult<int> Inspect(Stream source, CancellationToken token)
    {
        if (!source.CanSeek || source.Length is < 24 or > ImageInputInspector.MaximumFileBytes)
        { return Invalid(); }
        long previous = source.Position;
        try
        {
            source.Position = 0;
            Span<byte> header = stackalloc byte[8];
            source.ReadExactly(header);
            if (!header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) { return Invalid(); }
            int orientation = 1;
            bool found = false;
            byte[] crcBuffer = new byte[4096];
            Span<byte> checksum = stackalloc byte[4];
            while (source.Position < source.Length)
            {
                token.ThrowIfCancellationRequested();
                source.ReadExactly(header);
                uint length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
                long begin = source.Position;
                if (length > source.Length - begin - 4) { return Invalid(); }
                if (header[4..].SequenceEqual("eXIf"u8))
                {
                    if (found || length < 8) { return Invalid(); }
                    found = true;
                    var parsed = ReadIfd(source, begin, length, token);
                    if (parsed is not OperationResult<int>.Success value) { return parsed; }
                    orientation = value.Value;
                    source.Position = begin;
                    uint crc = UpdateCrc(uint.MaxValue, header[4..]);
                    long remaining = length;
                    while (remaining > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        int size = (int)Math.Min(remaining, crcBuffer.Length);
                        source.ReadExactly(crcBuffer.AsSpan(0, size));
                        crc = UpdateCrc(crc, crcBuffer.AsSpan(0, size));
                        remaining -= size;
                    }
                    source.ReadExactly(checksum);
                    if (BinaryPrimitives.ReadUInt32BigEndian(checksum) != ~crc) { return Invalid(); }
                }
                else { source.Position = begin + length + 4; }
                if (header[4..].SequenceEqual("IEND"u8))
                { return length == 0 && source.Position == source.Length ? new OperationResult<int>.Success(orientation) : Invalid(); }
            }
            return Invalid();
        }
        catch (EndOfStreamException) { return Invalid(); }
        finally { source.Position = previous; }
    }

    private static OperationResult<int> ReadIfd(Stream source, long begin, long length, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[8];
        source.ReadExactly(header);
        bool little = header[..2].SequenceEqual("II"u8);
        if ((!little && !header[..2].SequenceEqual("MM"u8)) || Word(header[2..4], little) != 42) { return Invalid(); }
        uint offset = Dword(header[4..], little);
        if (offset < 8 || offset > length - 2) { return Invalid(); }
        source.Position = begin + offset;
        Span<byte> countBytes = stackalloc byte[2];
        source.ReadExactly(countBytes);
        int count = Word(countBytes, little);
        if (offset + 2L + count * 12L + 4 > length) { return Invalid(); }
        Span<byte> entry = stackalloc byte[12];
        int orientation = 1;
        bool found = false;
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            source.ReadExactly(entry);
            if (Word(entry[..2], little) != 274) { continue; }
            if (found || Word(entry[2..4], little) != 3 || Dword(entry[4..8], little) != 1) { return Invalid(); }
            found = true;
            orientation = Word(entry[8..10], little);
            if (orientation is < 1 or > 8) { return Invalid(); }
        }
        return new OperationResult<int>.Success(orientation);
    }
    private static ushort Word(ReadOnlySpan<byte> bytes, bool little) => little
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    private static uint Dword(ReadOnlySpan<byte> bytes, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) { crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8); }
        return crc;
    }
    private static uint[] CreateCrcTable()
    {
        var result = new uint[256];
        for (uint index = 0; index < result.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++) { value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1; }
            result[index] = value;
        }
        return result;
    }
    private static OperationResult<int> Invalid() => new OperationResult<int>.Failure(AppError.Create(
        "InvalidPngOrientation", "PNG 方向元数据或块边界无效", "请选择完整有效的 PNG 图片"));
}
