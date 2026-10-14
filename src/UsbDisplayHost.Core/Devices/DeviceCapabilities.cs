using System.Collections.Immutable;
using UsbDisplayHost.Core.Operations;

namespace UsbDisplayHost.Core.Devices;

public enum CapabilityEvidence { Unknown, Declared, Verified, SourceMatched }
public enum DeviceCommand { SetBrightness, QueryState, SwitchMode, SetPower }

public sealed record DeviceCapabilities
{
    private DeviceCapabilities(int protocolVersion, int width, int height, int maximumFrameBytes,
        int maximumTransferBytes, string firmwareVersion, string source,
        CapabilityEvidence evidence, ImmutableHashSet<DeviceCommand> commands,
        bool hasPresentationFeedback)
    {
        ProtocolVersion = protocolVersion;
        Width = width;
        Height = height;
        MaximumFrameBytes = maximumFrameBytes;
        MaximumTransferBytes = maximumTransferBytes;
        FirmwareVersion = firmwareVersion;
        Source = source;
        Evidence = evidence;
        Commands = commands;
        HasPresentationFeedback = hasPresentationFeedback;
    }

    public int ProtocolVersion { get; }
    public int Width { get; }
    public int Height { get; }
    public int MaximumFrameBytes { get; }
    public int MaximumTransferBytes { get; }
    public string FirmwareVersion { get; }
    public string Source { get; }
    public CapabilityEvidence Evidence { get; }
    public ImmutableHashSet<DeviceCommand> Commands { get; }
    public bool HasPresentationFeedback { get; }
    public bool IsVerified => Evidence == CapabilityEvidence.Verified;
    public bool IsProtocolMatched => Evidence is CapabilityEvidence.Verified or CapabilityEvidence.SourceMatched;

    public static OperationResult<DeviceCapabilities> Create(int protocolVersion, int width, int height,
        int maximumFrameBytes, int maximumTransferBytes, string firmwareVersion, string source,
        CapabilityEvidence evidence, IEnumerable<DeviceCommand> commands, bool hasPresentationFeedback)
    {
        var commandSet = commands.ToImmutableHashSet();
        if (protocolVersion <= 0 || width <= 0 || height <= 0
            || maximumFrameBytes <= 0 || maximumTransferBytes <= 11
            || string.IsNullOrWhiteSpace(firmwareVersion) || string.IsNullOrWhiteSpace(source)
            || !Enum.IsDefined(evidence) || commandSet.Any(command => !Enum.IsDefined(command)))
        {
            return new OperationResult<DeviceCapabilities>.Failure(AppError.Create(
                "InvalidCapabilities", "设备能力字段不完整或超出有效范围", "请核对描述符和固件兼容记录"));
        }

        return new OperationResult<DeviceCapabilities>.Success(new DeviceCapabilities(
            protocolVersion, width, height, Math.Min(1_048_576, maximumFrameBytes),
            Math.Min(245_760, maximumTransferBytes), firmwareVersion, source, evidence,
            commandSet, hasPresentationFeedback));
    }
}
