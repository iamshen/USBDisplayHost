using System.Collections.Immutable;
using UsbDisplayHost.Core.Devices;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Core.Transport;

public readonly record struct JpegComponent(byte Id, byte HorizontalSampling, byte VerticalSampling);
public sealed record JpegFormat(byte SofMarker, byte Precision, ImmutableArray<JpegComponent> Components);
public sealed record JpegFrameInfo(int Width, int Height, JpegFormat Format);

// 格式清单由已冻结的固件兼容记录提供，主机解码成功不能生成这份记录。
public sealed class FirmwareJpegProfile
{
    private FirmwareJpegProfile(string firmwareVersion, string evidenceReference, ImmutableArray<JpegFormat> formats)
    {
        FirmwareVersion = firmwareVersion;
        EvidenceReference = evidenceReference;
        Formats = formats;
    }

    public string FirmwareVersion { get; }
    public string EvidenceReference { get; }
    public ImmutableArray<JpegFormat> Formats { get; }

    public static OperationResult<FirmwareJpegProfile> Create(string firmwareVersion, string evidenceReference,
        ImmutableArray<JpegFormat> formats)
    {
        if (string.IsNullOrWhiteSpace(firmwareVersion) || firmwareVersion.Length > 128
            || string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Length > 256
            || formats.IsDefaultOrEmpty || formats.Length > 16
            || formats.Any(format => format is null || !IsSupported(format)))
        {
            return new OperationResult<FirmwareJpegProfile>.Failure(AppError.Create("InvalidJpegProfile",
                "固件 JPEG 格式清单缺失或不受支持", "请提供匹配固件的兼容记录"));
        }
        return new OperationResult<FirmwareJpegProfile>.Success(new(firmwareVersion, evidenceReference, formats));
    }

    public bool Accepts(JpegFormat format) => Formats.Any(candidate =>
        candidate.SofMarker == format.SofMarker && candidate.Precision == format.Precision
        && candidate.Components.AsSpan().SequenceEqual(format.Components.AsSpan()));

    internal static bool IsSupported(JpegFormat format) =>
        format.SofMarker is 0xC0 or 0xC1 or 0xC2 && format.Precision == 8
        && !format.Components.IsDefaultOrEmpty && format.Components.Length is 1 or 3
        && format.Components.All(component => component.HorizontalSampling is >= 1 and <= 4
            && component.VerticalSampling is >= 1 and <= 4)
        && format.Components.Select(component => component.Id).Distinct().Count() == format.Components.Length
        && format.Components.Sum(component => component.HorizontalSampling * component.VerticalSampling) <= 10;
}

public interface IJpegFrameValidator
{
    Task<OperationResult<JpegFrameInfo>> ValidateAsync(EncodedFrame frame, DeviceCapabilities capabilities,
        FirmwareJpegProfile? profile, CancellationToken cancellationToken);
}
