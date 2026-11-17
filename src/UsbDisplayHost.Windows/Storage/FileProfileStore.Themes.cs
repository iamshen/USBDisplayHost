using System.Text.Json;
using UsbDisplayHost.Core.Configuration;
using UsbDisplayHost.Core.Operations;
using UsbDisplayHost.Core.Themes;

namespace UsbDisplayHost.Windows.Storage;

public sealed record SavedTheme(Guid Id, string Name, ThemeContent Content);
public sealed record SavedThemesResult(IReadOnlyList<SavedTheme> Themes, AppError? ReadWarning);

public sealed partial class FileProfileStore
{
    public const int MaximumThemeNameLength = 60;

    public Task<OperationResult<SavedThemesResult>> LoadThemesAsync(CancellationToken token) =>
        boundary.RunAsync<SavedThemesResult>(async () =>
        {
            List<SavedTheme> items = [];
            AppError? warning = null;
            if (Directory.Exists(paths.Themes))
            {
                var names = Directory.EnumerateFiles(paths.Themes).Select(Path.GetFileName)
                    .Where(name => name is not null && (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase)))
                    .Select(name => name![..name!.IndexOf('.')]).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (string name in names)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Guid.TryParseExact(name, "N", out Guid id) || id == Guid.Empty) { continue; }
                    var read = await storage.ReadAsync(paths.Theme(id), data => DecodeSavedTheme(data, id), token)
                        .ConfigureAwait(false);
                    if (read.Value is not null) { items.Add(read.Value); }
                    warning ??= read.Error;
                }
            }
            return new OperationResult<SavedThemesResult>.Success(new(
                items.OrderBy(theme => theme.Name, StringComparer.CurrentCulture).ToArray(), warning));
        }, "ThemeLibraryLoad", token);

    public Task<OperationResult<Unit>> SaveThemeAsync(SavedTheme theme, CancellationToken token) =>
        boundary.RunAsync(async () =>
        {
            if (theme.Id == Guid.Empty || !IsThemeNameValid(theme.Name)) { return InvalidSavedTheme<Unit>(); }
            if (ThemeValidation.Validate(theme.Content) is OperationResult<Unit>.Failure invalid) { return invalid; }
            var opened = OpenAssetForSave(theme.Content);
            if (opened is OperationResult<FileStream?>.Failure missing) { return new OperationResult<Unit>.Failure(missing.Error); }
            using var asset = ((OperationResult<FileStream?>.Success)opened).Value;
            return await storage.WriteAsync(paths.Theme(theme.Id), new SavedThemeData(theme.Id, theme.Name,
                ConfigurationCodec.Encode(theme.Content)), data => DecodeSavedTheme(data, theme.Id), token).ConfigureAwait(false);
        }, "ThemeLibrarySave", token);

    internal static OperationResult<SavedTheme> DecodeSavedTheme(JsonElement payload, Guid expectedId)
    {
        var data = payload.Deserialize<SavedThemeData>(AtomicJsonStorage.Options);
        return data is not null && data.Id == expectedId && IsThemeNameValid(data.Name) && data.Theme is not null
            && ConfigurationCodec.DecodeTheme(data.Theme) is OperationResult<ThemeContent>.Success content
            ? new OperationResult<SavedTheme>.Success(new(data.Id, data.Name, content.Value))
            : InvalidSavedTheme<SavedTheme>();
    }

    private static bool IsThemeNameValid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaximumThemeNameLength && !name.Any(char.IsControl);

    private static OperationResult<T> InvalidSavedTheme<T>() => new OperationResult<T>.Failure(
        AppError.Create("InvalidSavedTheme", "主题名称或保存内容无效", "请检查主题名称和元素后重试"));

    private sealed record SavedThemeData(Guid Id, string Name, ThemeData Theme);
}
