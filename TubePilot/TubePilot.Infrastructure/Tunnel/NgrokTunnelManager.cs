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
            logger.LogWarning("[Ngrok] NgrokAuthToken is missing. Tunnel disabled. See NGROK_SETUP.md for instructions.");
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

            logger.LogInformation("[Ngrok] Starting tunnel on port {Port}...", localPort);

            _process = Process.Start(new ProcessStartInfo
            {
                FileName = "ngrok",
                Arguments = $"http {localPort} --inspect=localhost:4040",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (_process is null)
            {
                logger.LogWarning("[Ngrok] Failed to start ngrok process.");
                return null;
            }

            logger.LogInformation("[Ngrok] Process started (PID: {Pid}). Waiting for tunnel...", _process.Id);

            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    logger.LogInformation("[ngrok] {Line}", e.Data);
            };
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    logger.LogInformation("[ngrok:out] {Line}", e.Data);
            };
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();

            for (var i = 0; i < 15; i++)
            {
                await Task.Delay(1000, ct);
                if (_process.HasExited)
                {
                    logger.LogWarning("[Ngrok] Process exited with code {Code}", _process.ExitCode);
                    return null;
                }
                PublicUrl = await TryGetUrlFromApi(logger);
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
            logger.LogWarning("[Ngrok] ngrok not found in PATH. Tunnel disabled. See NGROK_SETUP.md for instructions.");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("[Ngrok] Tunnel setup failed: {Msg}", ex.Message);
            return null;
        }
    }

    private static async Task<string?> TryGetUrlFromApi(ILogger logger)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetFromJsonAsync<NgrokApiResponse>(NgrokApiUrl);
            var url = response?.Tunnels?
                .FirstOrDefault(t => t.PublicUrl?.StartsWith("https") == true)
                ?.PublicUrl;
            if (url is null)
                logger.LogInformation("[Ngrok] API responded, tunnels: {Count}, no HTTPS yet", response?.Tunnels?.Length ?? 0);
            return url;
        }
        catch (Exception ex)
        {
            logger.LogInformation("[Ngrok] API not ready: {Msg}", ex.Message);
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
