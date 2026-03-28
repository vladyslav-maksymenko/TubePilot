using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Drive.Options;
using TubePilot.Infrastructure.GoogleSheets.Options;

namespace TubePilot.Infrastructure.GoogleSheets;

internal sealed class GoogleSheetsLogger : IGoogleSheetsLogger
{
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly IOptionsMonitor<GoogleSheetsOptions> _sheetsOptions;
    private readonly IOptionsMonitor<DriveOptions> _driveOptions;
    private readonly ILogger<GoogleSheetsLogger> _logger;
    private readonly Lazy<SheetsService?> _service;
    private AuditSheetState? _cachedState;

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

    internal GoogleSheetsLogger(
        IOptionsMonitor<GoogleSheetsOptions> sheetsOptions,
        IOptionsMonitor<DriveOptions> driveOptions,
        ILogger<GoogleSheetsLogger> logger,
        SheetsService serviceOverride)
    {
        _sheetsOptions = sheetsOptions;
        _driveOptions = driveOptions;
        _logger = logger;
        _service = new Lazy<SheetsService?>(() => serviceOverride);
    }

    public async Task LogUploadAsync(
        string channel,
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

        try
        {
            var state = await GetOrInitializeAuditSheetStateAsync(service, spreadsheetId, options.SheetName, ct);
            if (state is null)
            {
                return;
            }

            var appendRequest = new AppendCellsRequest
            {
                SheetId = state.SheetId,
                Fields = "userEnteredValue",
                Rows =
                [
                    GoogleSheetsAuditSheet.BuildAuditRow(
                        DateTimeOffset.UtcNow,
                        channel,
                        sourceFile,
                        title,
                        youtubeId,
                        youtubeUrl,
                        status,
                        scheduledAtUtc)
                ]
            };

            await service.Spreadsheets.BatchUpdate(
                new BatchUpdateSpreadsheetRequest
                {
                    Requests = new List<Request>
                    {
                        new()
                        {
                            AppendCells = appendRequest
                        }
                    }
                },
                spreadsheetId).ExecuteAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append Google Sheets audit row.");
        }
    }

    private async Task<AuditSheetState?> GetOrInitializeAuditSheetStateAsync(
        SheetsService service,
        string spreadsheetId,
        string sheetName,
        CancellationToken ct)
    {
        if (_cachedState is { SpreadsheetId: var cachedSpreadsheetId, SheetName: var cachedSheetName } &&
            string.Equals(cachedSpreadsheetId, spreadsheetId, StringComparison.Ordinal) &&
            string.Equals(cachedSheetName, sheetName, StringComparison.Ordinal))
        {
            return _cachedState;
        }

        await _initializationGate.WaitAsync(ct);
        try
        {
            if (_cachedState is { SpreadsheetId: var refreshedSpreadsheetId, SheetName: var refreshedSheetName } &&
                string.Equals(refreshedSpreadsheetId, spreadsheetId, StringComparison.Ordinal) &&
                string.Equals(refreshedSheetName, sheetName, StringComparison.Ordinal))
            {
                return _cachedState;
            }

            var spreadsheetRequest = service.Spreadsheets.Get(spreadsheetId);
            spreadsheetRequest.Fields = "sheets(properties,conditionalFormats,bandedRanges)";
            var spreadsheet = await spreadsheetRequest.ExecuteAsync(ct);
            var sheet = spreadsheet.Sheets?.FirstOrDefault(s =>
                string.Equals(s.Properties?.Title, sheetName, StringComparison.OrdinalIgnoreCase));

            if (sheet is null)
            {
                await service.Spreadsheets.BatchUpdate(
                    new BatchUpdateSpreadsheetRequest
                    {
                        Requests =
                        [
                            new Request
                            {
                                AddSheet = new AddSheetRequest
                                {
                                    Properties = new SheetProperties
                                    {
                                        Title = sheetName
                                    }
                                }
                            }
                        ]
                    },
                    spreadsheetId).ExecuteAsync(ct);

                spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync(ct);
                sheet = spreadsheet.Sheets?.FirstOrDefault(s =>
                    string.Equals(s.Properties?.Title, sheetName, StringComparison.OrdinalIgnoreCase));
            }

            if (sheet?.Properties?.SheetId is not int sheetId)
            {
                throw new InvalidOperationException($"Could not resolve Google Sheets tab '{sheetName}'.");
            }

            var firstRowRange = await service.Spreadsheets.Values
                .Get(spreadsheetId, $"{sheetName}!A1:J1")
                .ExecuteAsync(ct);
            var firstRowValues = firstRowRange.Values?.FirstOrDefault();
            var headerKind = GoogleSheetsAuditSheet.AnalyzeHeaderRow(firstRowValues);
            var hasAnyFirstRowValues = GoogleSheetsAuditSheet.HasAnyValues(firstRowValues);
            var existingColumnCount = sheet.Properties?.GridProperties?.ColumnCount ?? GoogleSheetsAuditSheet.ColumnCount;

            var existingRuleCount = sheet.ConditionalFormats?.Count ?? 0;
            var normalizationRequests = new List<Request>();
            normalizationRequests.AddRange(
                GoogleSheetsAuditSheet.BuildPreNormalizationRequests(
                    sheetId,
                    headerKind,
                    hasAnyFirstRowValues,
                    existingColumnCount));
            if (sheet.BandedRanges is not null)
            {
                foreach (var bandedRange in sheet.BandedRanges)
                {
                    if (bandedRange?.BandedRangeId is not int bandedRangeId)
                    {
                        continue;
                    }

                    normalizationRequests.Add(new Request
                    {
                        DeleteBanding = new DeleteBandingRequest
                        {
                            BandedRangeId = bandedRangeId
                        }
                    });
                }
            }
            normalizationRequests.AddRange(GoogleSheetsAuditSheet.BuildNormalizationRequests(sheetId, existingRuleCount));
            if (normalizationRequests.Count > 0)
            {
                await service.Spreadsheets.BatchUpdate(
                    new BatchUpdateSpreadsheetRequest
                    {
                        Requests = normalizationRequests.ToList()
                    },
                    spreadsheetId).ExecuteAsync(ct);
            }

            _cachedState = new AuditSheetState(spreadsheetId, sheetName, sheetId);
            return _cachedState;
        }
        finally
        {
            _initializationGate.Release();
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

    private sealed record AuditSheetState(string SpreadsheetId, string SheetName, int SheetId);
}
