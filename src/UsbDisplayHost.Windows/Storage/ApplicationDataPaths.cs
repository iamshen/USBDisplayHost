using System.Security.Cryptography;
using System.Text;
using UsbDisplayHost.Core.Devices;

namespace UsbDisplayHost.Windows.Storage;

public sealed class ApplicationDataPaths
{
    public ApplicationDataPaths(string? dataDirectory = null)
    {
        Root = Path.GetFullPath(dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbDisplayHost.Data"));
    }

    public string Root { get; }
    public string Assets => Path.Combine(Root, "assets");
    public string Logs => Path.Combine(Root, "diagnostics", "logs");
    public string Settings => Path.Combine(Root, "application-settings.json");
    public string PendingAssetCleanup => Path.Combine(Root, "asset-cleanup.pending.json");
    public string Profiles => Path.Combine(Root, "profiles");
    public string Drafts => Path.Combine(Root, "drafts");
    public string DeviceSettingsDrafts => Path.Combine(Root, "device-settings-drafts");
    public string DeviceSettingsDraft(DeviceKey? deviceKey) => Path.Combine(DeviceSettingsDrafts,
        (deviceKey is null ? "offline" : FileKey(deviceKey)) + ".json");

    public string Profile(DeviceKey deviceKey) => Path.Combine(Profiles, FileKey(deviceKey) + ".json");
    public string Draft(DeviceKey? deviceKey) => Path.Combine(Drafts,
        (deviceKey is null ? "offline" : FileKey(deviceKey)) + ".json");

    private static string FileKey(DeviceKey key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Value)));
}
