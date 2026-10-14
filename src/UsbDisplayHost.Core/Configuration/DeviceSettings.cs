using System.Collections.Immutable;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Configuration;

public enum DisplayRotation { Degrees0 = 0, Degrees90 = 90, Degrees180 = 180, Degrees270 = 270 }

public sealed record DeviceSettings
{
    private DeviceSettings(DisplayRotation rotation, int framesPerSecond, int jpegQuality)
    {
        Rotation = rotation;
        FramesPerSecond = framesPerSecond;
        JpegQuality = jpegQuality;
    }

    public DisplayRotation Rotation { get; }
    public int FramesPerSecond { get; }
    public int JpegQuality { get; }
    public static DeviceSettings Default { get; } = new(DisplayRotation.Degrees0, 5, 80);

    public static OperationResult<DeviceSettings> Create(DisplayRotation rotation, int framesPerSecond, int jpegQuality)
    {
        if (!Enum.IsDefined(rotation) || framesPerSecond is < 1 or > 5 || jpegQuality is < 70 or > 85)
        {
            return new OperationResult<DeviceSettings>.Failure(AppError.Create(
                "InvalidDeviceSettings", "方向、帧率或画质超出允许范围", "请选择 0/90/180/270°、1～5fps 和画质 70～85"));
        }

        return new OperationResult<DeviceSettings>.Success(new DeviceSettings(rotation, framesPerSecond, jpegQuality));
    }

    public ImmutableArray<int> GetEncodingQualities()
    {
        var qualities = ImmutableArray.CreateBuilder<int>(4);
        for (int quality = JpegQuality; quality > 70; quality -= 5)
        {
            qualities.Add(quality);
        }
        qualities.Add(70);
        return qualities.ToImmutable();
    }
}
