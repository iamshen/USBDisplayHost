using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Transport;

namespace UsbDisplayHost.Desktop.Services;

internal static class ThemeApplicationResultFormatter
{
    internal static string Format(ThemeApplicationResult value, string target, long currentRevision)
    {
        string sent = value.Transmission switch
        {
            OperationResult<FrameTransferReceipt>.Success => "已发送",
            OperationResult<FrameTransferReceipt>.Failure => "发送失败",
            OperationResult<FrameTransferReceipt>.Unknown => "发送未确认",
            _ => "发送已取消"
        };
        string saved = value.SaveState switch { ThemeSaveState.Saved => "已保存恢复配置", ThemeSaveState.Failed => "保存失败",
            ThemeSaveState.Unknown => "保存待核实", _ => "未保存恢复配置" };
        string active = value.ActiveCommitted ? "活动内容已提交" : "活动内容未提交";
        return $"设备 {target}，修订 {value.FrozenDraftRevision}：{sent}；{active}；{saved}；呈现反馈未提供"
            + (currentRevision != value.FrozenDraftRevision ? "。仍有新编辑未应用" : string.Empty);
    }
}
