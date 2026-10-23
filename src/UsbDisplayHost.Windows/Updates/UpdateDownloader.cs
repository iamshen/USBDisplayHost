using System.Net.Http;
using Velopack.Sources;

namespace UsbDisplayHost.Windows.Updates;

internal sealed class UpdateDownloader(Action<long, long?> report) : HttpClientFileDownloader
{
    // Velopack 的检查入口没有取消参数，由下载适配器持有当前串行操作的取消令牌。
    public CancellationToken RequestToken { get; set; }

    public override async Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout)
    {
        using var client = CreateHttpClient(headers, 0.5);
        return await client.GetStringAsync(url, RequestToken).ConfigureAwait(false);
    }

    public override async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout)
    {
        using var client = CreateHttpClient(headers, 0.5);
        return await client.GetByteArrayAsync(url, RequestToken).ConfigureAwait(false);
    }

    public override async Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers, double timeout, CancellationToken cancelToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        using var client = CreateHttpClient(headers, 0.5);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        await using var destination = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[81920];
        long received = 0;
        long lastReport = Environment.TickCount64;
        report(0, total);
        while (true)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            int count = await source.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
            if (count == 0) { break; }
            await destination.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
            received += count;
            if (Environment.TickCount64 - lastReport >= 100)
            {
                report(received, total);
                if (total is > 0) { progress((int)Math.Min(100, received * 100 / total.Value)); }
                lastReport = Environment.TickCount64;
            }
        }
        report(received, total);
        progress(100);
    }
}
