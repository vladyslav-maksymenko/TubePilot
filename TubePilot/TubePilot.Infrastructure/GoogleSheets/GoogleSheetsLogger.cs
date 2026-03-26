using Google.Apis.Auth.OAuth2;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Drive.Options;
using TubePilot.Infrastructure.GoogleSheets.Options;

namespace TubePilot.Infrastructure.GoogleSheets;

internal sealed class GoogleSheetsLogger : IGoogleSheetsLogger
{
    private readonly IOptionsMonitor<GoogleSheetsOptions> _sheetsOptions;
    private readonly IOptionsMonitor<DriveOptions> _driveOptions;
    private readonly ILogger<GoogleSheetsLogger> _logger;
    private readonly Lazy<SheetsService?> _service;

    public GoogleSheetsLogger(
        IOptionsMonitor<GoogleSheetsOptions> sheetsOptions,
        IOptionsMonitor<DriveOptions> driveOptions,
        ILogger<GoogleSheetsLogger> logger)
    {
        _sheetsOptions = sheetsOptions;
        _driveOptions = driveOptions;
        _logger = logger;
        _service = new Lazy<SheetsService?>(CreateService);
    }

    public async Task LogUploadAsync(
        string sourceFile,
        string title,
        string youtubeId,
        string youtubeUrl,
        string status,
        DateTimeOffset? scheduledAtUtc,
        CancellationToken ct = default)
    {
        var service = _service.Value;
        if (service is null)
        {
            return;
        }

        var options = _sheetsOptions.CurrentValue;
        var spreadsheetId = options.SpreadsheetId;
        if (string.IsNullOrWhiteSpace(spreadsheetId))
        {
            return;
        }

        var row = new List<object>
        {
            DateTimeOffset.UtcNow.ToString("O"),
            sourceFile,
            title,
            youtubeId,
            youtubeUrl,
            status,
            scheduledAtUtc?.ToString("O") ?? string.Empty
        };

        var valueRange = new ValueRange
        {
            Values = [row]
        };

        var request = service.Spreadsheets.Values.Append(valueRange, spreadsheetId, $"{options.SheetName}!A:G");
        request.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
        request.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;

        try
        {
            await request.ExecuteAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append Google Sheets audit row.");
        }
    }

    private SheetsService? CreateService()
    {
        var drive = _driveOptions.CurrentValue;
        var sheets = _sheetsOptions.CurrentValue;

        if (string.IsNullOrWhiteSpace(sheets.SpreadsheetId) ||
            drive.ServiceAccount is null ||
            string.IsNullOrWhiteSpace(drive.ServiceAccount.ClientEmail) ||
            string.IsNullOrWhiteSpace(drive.ServiceAccount.PrivateKey))
        {
            _logger.LogDebug("Google Sheets logging is disabled because configuration is incomplete.");
            return null;
        }

        var credential = new ServiceAccountCredential(new ServiceAccountCredential.Initializer(drive.ServiceAccount.ClientEmail)
        {
            Scopes = [SheetsService.Scope.Spreadsheets]
        }.FromPrivateKey(drive.ServiceAccount.PrivateKey));

        return new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "TubePilot"
        });
    }
}
