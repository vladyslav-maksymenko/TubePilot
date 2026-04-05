namespace TubePilot.Infrastructure.GoogleSheets.Options;

public sealed record GoogleSheetsOptions
{
    public const string SectionName = "GoogleSheets";

    public string? SpreadsheetId { get; init; }

    public string SheetName { get; init; } = "Audit";
}
