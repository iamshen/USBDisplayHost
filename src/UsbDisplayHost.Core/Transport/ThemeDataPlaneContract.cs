using System.Collections.Immutable;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Transport;

public enum ProbeWordOrder { LittleEndian, BigEndian }

// 由固件冻结记录构建；没有默认 ready 值、EP0 request 或颜色格式。
public sealed class ThemeDataPlaneContract
{
    private ThemeDataPlaneContract(string firmwareVersion, string evidenceReference, ControlSetup probe,
        ImmutableArray<byte> expected, ImmutableArray<byte> masks, int widthOffset, int heightOffset,
        ProbeWordOrder wordOrder, bool probeSafeWhileOff, byte colorFormat,
        ControlSetup? edidProbe = null, int edidTimingOffset = 54)
    {
        FirmwareVersion = firmwareVersion; EvidenceReference = evidenceReference; Probe = probe;
        Expected = expected; Masks = masks; WidthOffset = widthOffset; HeightOffset = heightOffset;
        WordOrder = wordOrder; ProbeSafeWhileOff = probeSafeWhileOff; ColorFormat = colorFormat;
        EdidProbe = edidProbe; EdidTimingOffset = edidTimingOffset;
    }
    public string FirmwareVersion { get; }
    public string EvidenceReference { get; }
    public ControlSetup Probe { get; }
    public ImmutableArray<byte> Expected { get; }
    public ImmutableArray<byte> Masks { get; }
    public int WidthOffset { get; }
    public int HeightOffset { get; }
    public ProbeWordOrder WordOrder { get; }
    public bool ProbeSafeWhileOff { get; }
    public byte ColorFormat { get; }
    public ControlSetup? EdidProbe { get; }
    public int EdidTimingOffset { get; }
    public const int EdidBlockBytes = 128;

    public static OperationResult<ThemeDataPlaneContract> CreateWithEdid(string firmwareVersion, string evidenceReference,
        ControlSetup statusProbe, ImmutableArray<byte> expected, ImmutableArray<byte> masks,
        ControlSetup edidProbe, int timingOffset, bool queriesReadOnly, bool queriesSafeWhileOff, byte colorFormat)
    {
        if (string.IsNullOrWhiteSpace(firmwareVersion) || firmwareVersion.Length > 128
            || string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Length > 256
            || !queriesReadOnly || statusProbe.RequestType != 0xC1 || edidProbe.RequestType != 0xC1
            || statusProbe.Request != 1 || statusProbe.Value != 0 || edidProbe.Request != 2
            || statusProbe.Index != edidProbe.Index || edidProbe.Value != 0
            || expected.IsDefault || expected.Length != 1 || masks.IsDefault || expected.Length != masks.Length
            || timingOffset is not (54 or 72 or 90 or 108)) { return Invalid(); }
        bool hasReadyMask = false;
        for (int index = 0; index < masks.Length; index++)
        {
            if ((expected[index] & ~masks[index]) != 0) { return Invalid(); }
            hasReadyMask |= masks[index] != 0;
        }
        if (!hasReadyMask) { return Invalid(); }
        return new OperationResult<ThemeDataPlaneContract>.Success(new(firmwareVersion, evidenceReference,
            statusProbe, expected, masks, -1, -1, ProbeWordOrder.LittleEndian, queriesSafeWhileOff, colorFormat,
            edidProbe, timingOffset));
    }

    public static OperationResult<ThemeDataPlaneContract> Create(string firmwareVersion, string evidenceReference,
        ControlSetup probe, ImmutableArray<byte> expected, ImmutableArray<byte> masks,
        int widthOffset, int heightOffset, ProbeWordOrder wordOrder, bool probeReadOnly,
        bool probeSafeWhileOff, byte colorFormat)
    {
        if (string.IsNullOrWhiteSpace(firmwareVersion) || firmwareVersion.Length > 128
            || string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Length > 256
            || !probeReadOnly || (probe.RequestType & 0xE0) != 0xC0 || (probe.RequestType & 31) > 2
            || expected.IsDefaultOrEmpty || masks.IsDefault || expected.Length != masks.Length
            || expected.Length is < 5 or > 4096 || widthOffset < 0 || heightOffset < 0
            || widthOffset > expected.Length - 2 || heightOffset > expected.Length - 2
            || Math.Abs(widthOffset - heightOffset) < 2 || !Enum.IsDefined(wordOrder))
        {
            return Invalid();
        }
        bool hasReadyMask = false;
        for (int index = 0; index < masks.Length; index++)
        {
            if ((expected[index] & ~masks[index]) != 0) { return Invalid(); }
            bool dimension = index == widthOffset || index == widthOffset + 1 || index == heightOffset || index == heightOffset + 1;
            if (!dimension && masks[index] != 0) { hasReadyMask = true; }
        }
        if (!hasReadyMask) { return Invalid(); }
        return new OperationResult<ThemeDataPlaneContract>.Success(new(firmwareVersion, evidenceReference,
            probe, expected, masks, widthOffset, heightOffset, wordOrder, probeSafeWhileOff, colorFormat));
    }

    public bool MatchesProbe(ReadOnlySpan<byte> bytes, int width, int height)
    {
        if (bytes.Length != Expected.Length) { return false; }
        for (int index = 0; index < bytes.Length; index++)
        {
            if ((bytes[index] & Masks[index]) != Expected[index]) { return false; }
        }
        return EdidProbe is not null || ReadWord(bytes, WidthOffset) == width && ReadWord(bytes, HeightOffset) == height;
    }

    public bool MatchesEdid(ReadOnlySpan<byte> bytes, int width, int height)
    {
        if (EdidProbe is null || bytes.Length != EdidBlockBytes || width <= 0 || height <= 0
            || !bytes[..8].SequenceEqual<byte>([0, 255, 255, 255, 255, 255, 255, 0])
            || bytes[18] != 1 || bytes[19] is not (3 or 4)) { return false; }
        int checksum = 0;
        foreach (byte value in bytes) { checksum += value; }
        if ((checksum & 255) != 0) { return false; }
        var timing = bytes.Slice(EdidTimingOffset, 18);
        if (timing[0] == 0 && timing[1] == 0) { return false; }
        int actualWidth = timing[2] | ((timing[4] & 0xF0) << 4);
        int actualHeight = timing[5] | ((timing[7] & 0xF0) << 4);
        // 核对冻结契约指定的 DTD，不把任意支持模式或占位描述符当作目标尺寸。
        return actualWidth == width && actualHeight == height;
    }

    private int ReadWord(ReadOnlySpan<byte> bytes, int offset) => WordOrder == ProbeWordOrder.BigEndian
        ? (bytes[offset] << 8) | bytes[offset + 1] : bytes[offset] | (bytes[offset + 1] << 8);
    private static OperationResult<ThemeDataPlaneContract> Invalid() => new OperationResult<ThemeDataPlaneContract>.Failure(
        AppError.Create("InvalidDataPlaneContract", "主题数据面契约字段不完整或不受支持", "请冻结 ready 位、尺寸字段、EP0 和颜色格式"));
}
