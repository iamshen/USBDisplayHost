using System.Collections.Immutable;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Transport;

// 此处只核验受限 JPEG 容器；完整像素解码由 Rendering 适配器承担。
public static class JpegStructureInspector
{
    public static OperationResult<JpegFrameInfo> Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 4 or > 1_048_576 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return Invalid();
        }
        int position = 2;
        JpegFrameInfo? info = null;
        bool entropy = false;
        bool hadScan = false;
        int scanBytes = 0;
        int quantizationTables = 0, dcTables = 0, acTables = 0, frameQuantizationTables = 0;
        int restartInterval = 0, nextRestart = 0;
        while (position < bytes.Length)
        {
            if (entropy && bytes[position] != 0xFF)
            {
                position++;
                scanBytes++;
                continue;
            }
            if (bytes[position++] != 0xFF) { return Invalid(); }
            int prefixCount = 1;
            while (position < bytes.Length && bytes[position] == 0xFF) { position++; prefixCount++; }
            if (position == bytes.Length) { return Invalid(); }
            byte marker = bytes[position++];
            if (entropy && marker == 0x00)
            {
                if (prefixCount != 1) { return Invalid(); }
                scanBytes++;
                continue;
            }
            if (entropy && marker is >= 0xD0 and <= 0xD7)
            {
                if (restartInterval == 0 || scanBytes == 0 || marker != 0xD0 + nextRestart) { return Invalid(); }
                nextRestart = (nextRestart + 1) % 8;
                scanBytes = 0;
                continue;
            }
            if (entropy)
            {
                if (scanBytes == 0) { return Invalid(); }
                entropy = false;
            }
            if (marker == 0xD9)
            {
                return position == bytes.Length && hadScan && info is not null
                    ? new OperationResult<JpegFrameInfo>.Success(info) : Invalid();
            }
            // 不接收拼接图、独立重启、DNL、层次或算术编码；不能靠跳过未知字段放行。
            if (marker is 0x00 or 0x01 or 0xD8 or >= 0xD0 and <= 0xD7
                || position + 2 > bytes.Length) { return Invalid(); }
            int length = (bytes[position] << 8) | bytes[position + 1];
            if (length < 2 || length > bytes.Length - position) { return Invalid(); }
            var payload = bytes.Slice(position + 2, length - 2);
            position += length;
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (info is not null || hadScan || payload.Length < 6) { return Invalid(); }
                int componentCount = payload[5];
                if (componentCount is not (1 or 3) || payload.Length != 6 + 3 * componentCount) { return Invalid(); }
                int height = (payload[1] << 8) | payload[2];
                int width = (payload[3] << 8) | payload[4];
                if (height == 0 || width == 0 || (long)width * height > 20_000_000) { return Invalid(); }
                var components = ImmutableArray.CreateBuilder<JpegComponent>(componentCount);
                for (int index = 0; index < componentCount; index++)
                {
                    int offset = 6 + index * 3;
                    if (payload[offset + 2] > 3) { return Invalid(); }
                    frameQuantizationTables |= 1 << payload[offset + 2];
                    components.Add(new(payload[offset], (byte)(payload[offset + 1] >> 4), (byte)(payload[offset + 1] & 15)));
                }
                var format = new JpegFormat(marker, payload[0], components.MoveToImmutable());
                if (!FirmwareJpegProfile.IsSupported(format)) { return Invalid(); }
                info = new(width, height, format);
            }
            else if (marker == 0xDA)
            {
                if (info is null || payload.Length < 4) { return Invalid(); }
                int count = payload[0];
                if (count < 1 || count > info.Format.Components.Length || payload.Length != 1 + 2 * count + 3) { return Invalid(); }
                if ((frameQuantizationTables & quantizationTables) != frameQuantizationTables) { return Invalid(); }
                for (int index = 0; index < count; index++)
                {
                    byte id = payload[1 + index * 2];
                    if (!ContainsId(info.Format, id) || (payload[2 + index * 2] >> 4) > 3
                        || (payload[2 + index * 2] & 15) > 3) { return Invalid(); }
                    for (int previous = 0; previous < index; previous++)
                    {
                        if (payload[1 + previous * 2] == id) { return Invalid(); }
                    }
                }
                int spectralStart = payload[^3], spectralEnd = payload[^2];
                int high = payload[^1] >> 4, low = payload[^1] & 15;
                if (info.Format.SofMarker != 0xC2
                    ? spectralStart != 0 || spectralEnd != 63 || high != 0 || low != 0
                    : spectralStart > spectralEnd || spectralEnd > 63 || high > 13 || low > 13
                        || (spectralStart == 0 && spectralEnd != 0) || (spectralStart > 0 && count != 1)
                        || (high != 0 && high != low + 1)) { return Invalid(); }
                for (int index = 0; index < count; index++)
                {
                    int selectors = payload[2 + index * 2];
                    bool needsDc = info.Format.SofMarker != 0xC2 || (spectralStart == 0 && high == 0);
                    bool needsAc = info.Format.SofMarker != 0xC2 || spectralStart > 0;
                    if ((needsDc && (dcTables & (1 << (selectors >> 4))) == 0)
                        || (needsAc && (acTables & (1 << (selectors & 15))) == 0)) { return Invalid(); }
                }
                hadScan = entropy = true;
                scanBytes = 0;
                nextRestart = 0;
            }
            else if (marker == 0xDB)
            {
                int offset = 0;
                if (payload.IsEmpty) { return Invalid(); }
                while (offset < payload.Length)
                {
                    int table = payload[offset++];
                    // 当前发送入口仅支持 8 位量化表，不猜测固件对 16 位表的支持。
                    if (table > 3 || payload.Length - offset < 64) { return Invalid(); }
                    for (int index = 0; index < 64; index++)
                    {
                        if (payload[offset + index] == 0) { return Invalid(); }
                    }
                    offset += 64;
                    quantizationTables |= 1 << table;
                }
            }
            else if (marker == 0xC4)
            {
                int offset = 0;
                if (payload.IsEmpty) { return Invalid(); }
                while (offset < payload.Length)
                {
                    if (payload.Length - offset < 17) { return Invalid(); }
                    int table = payload[offset++];
                    if ((table >> 4) > 1 || (table & 15) > 3) { return Invalid(); }
                    int symbolCount = 0, availableCodes = 1;
                    for (int lengthIndex = 0; lengthIndex < 16; lengthIndex++)
                    {
                        int count = payload[offset++];
                        symbolCount += count;
                        availableCodes = availableCodes * 2 - count;
                        if (availableCodes <= 0) { return Invalid(); }
                    }
                    if (symbolCount is < 1 or > 256 || symbolCount > payload.Length - offset) { return Invalid(); }
                    offset += symbolCount;
                    if ((table >> 4) == 0) { dcTables |= 1 << (table & 15); }
                    else { acTables |= 1 << (table & 15); }
                }
            }
            else if (marker is not (0xC4 or 0xDB or 0xDD or 0xFE or >= 0xE0 and <= 0xEF))
            {
                return Invalid();
            }
            else if (marker == 0xDD)
            {
                if (payload.Length != 2) { return Invalid(); }
                restartInterval = (payload[0] << 8) | payload[1];
            }
        }
        return Invalid();
    }

    private static bool ContainsId(JpegFormat format, byte id)
    {
        foreach (var component in format.Components) { if (component.Id == id) { return true; } }
        return false;
    }

    private static OperationResult<JpegFrameInfo> Invalid() => new OperationResult<JpegFrameInfo>.Failure(
        AppError.Create("InvalidJpegStructure", "JPEG 结构不完整、超限或不受支持", "请重新生成设备画面并核对固件格式"));
}
