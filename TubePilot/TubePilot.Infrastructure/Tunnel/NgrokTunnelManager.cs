using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TubePilot.Infrastructure.Tunnel;

internal sealed class NgrokTunnelManager : IAsyncDisposable
{
    private const string NgrokApiUrl = "http://localhost:4040/api/tunnels";

    private Process? _process;

    public string? PublicUrl { get; private set; }

    public async Task<string?> StartAsync(int localPort, string authToken, ILogger logger, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authToken))
        {
            logger.LogWarning("[Ngrok] NgrokAuthToken is missing. Tunnel disabled. See README for setup instructions.");
            return null;
        }

        try
        {
            // Set authtoken
            var configProc = Process.Start(new ProcessStartInfo
            {
                FileName = "ngrok",
                Arguments = $"config add-authtoken {authToken}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (configProc is not null)
                await configProc.WaitForExitAsync(ct);

            // Start tunnel
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = "ngrok",
                Arguments = $"http {localPort} --log stderr",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (_process is null) return null;

            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    logger.LogDebug("[ngrok] {Line}", e.Data);
            };
            _process.BeginErrorReadLine();

            for (var i = 0; i < 15; i++)
            {
                await Task.Delay(1000, ct);
                PublicUrl = await TryGetUrlFromApi();
                if (PublicUrl is not null) break;
            }

            if (PublicUrl is not null)
                logger.LogInformation("[Ngrok] Tunnel active: {Url}", PublicUrl);
            else
                logger.LogWarning("[Ngrok] Failed to detect tunnel URL within timeout.");

            return PublicUrl;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("[Ngrok] ngrok not found in PATH. Tunnel disabled. See README for setup instructions.");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("[Ngrok] Tunnel setup failed: {Msg}", ex.Message);
            return null;
        }
    }

    private static async Task<string?> TryGetUrlFromApi()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await http.GetFromJsonAsync<NgrokApiResponse>(NgrokApiUrl);
            return response?.Tunnels?
                .FirstOrDefault(t => t.PublicUrl?.StartsWith("https") == true)
                ?.PublicUrl;
        }
        catch
        {
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

    private sealed record NgrokApiResponse([property: JsonPropertyName("tunnels")] NgrokTunnel[]? Tunnels);
    private sealed record NgrokTunnel([property: JsonPropertyName("public_url")] string? PublicUrl);
}
