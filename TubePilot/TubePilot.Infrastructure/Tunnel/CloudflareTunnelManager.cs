using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace TubePilot.Infrastructure.Tunnel;

internal sealed partial class CloudflareTunnelManager : IAsyncDisposable
{
    private static readonly string BinaryPath = Path.Combine(AppContext.BaseDirectory, "cloudflared.exe");

    private static string GetDownloadUrl() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-arm64.exe",
        _ => "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe"
    };

    private Process? _process;

    public string? PublicUrl { get; private set; }

    public async Task<string?> StartAsync(int localPort, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(BinaryPath))
            {
                logger.LogInformation("[Cloudflare] Downloading cloudflared...");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                var bytes = await http.GetByteArrayAsync(GetDownloadUrl(), ct);
                await File.WriteAllBytesAsync(BinaryPath, bytes, ct);
                logger.LogInformation("[Cloudflare] Downloaded to {Path}", BinaryPath);
            }

            _process = Process.Start(new ProcessStartInfo
            {
                FileName = BinaryPath,
                Arguments = $"tunnel --url http://localhost:{localPort}",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (_process is null) return null;

            var tcs = new TaskCompletionSource<string?>();
            using var reg = ct.Register(() => tcs.TrySetResult(null));

            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                logger.LogDebug("[cloudflared] {Line}", e.Data);

                var match = TunnelUrlRegex().Match(e.Data);
                if (match.Success && !match.Value.Contains("api.trycloudflare.com"))
                    tcs.TrySetResult(match.Value);
            };

            _process.BeginErrorReadLine();

            PublicUrl = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(20), ct))
                == tcs.Task ? tcs.Task.Result : null;

            if (PublicUrl is not null)
                logger.LogInformation("[Cloudflare] Tunnel active: {Url}", PublicUrl);
            else
                logger.LogWarning("[Cloudflare] Failed to detect tunnel URL within timeout.");

            return PublicUrl;
        }
        catch (Exception ex)
        {
            logger.LogWarning("[Cloudflare] Tunnel setup failed: {Msg}. Links will use BaseUrl from config.", ex.Message);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false } p)
        {
            p.Kill(entireProcessTree: true);
            await p.WaitForExitAsync();
        }
        _process?.Dispose();
    }

    [GeneratedRegex(@"https://[a-z0-9\-]+\.trycloudflare\.com")]
    private static partial Regex TunnelUrlRegex();
}
