using Google.Apis.Sheets.v4.Data;
using TubePilot.Infrastructure.GoogleSheets;

namespace TubePilot.Infrastructure.Tests;

public sealed class GoogleSheetsAuditSheetTests
{
    [Fact]
    public void AnalyzeHeaderRow_WhenExpectedHeader_ReturnsExpected()
    {
        var row = GoogleSheetsAuditSheet.HeaderKeys.Cast<object>().ToList();
        var kind = GoogleSheetsAuditSheet.AnalyzeHeaderRow(row);
        Assert.Equal(GoogleSheetsAuditSheet.HeaderKind.Expected, kind);
    }

    [Fact]
    public void BuildAuditRow_UsesIsoUtcStrings_AndClickableYoutubeLink()
    {
        var row = GoogleSheetsAuditSheet.BuildAuditRow(
            DateTimeOffset.Parse("2026-03-27T08:15:30+00:00"),
            "My Channel",
            "source.mp4",
            "My title",
            "abc123",
            "https://youtube.test/watch?v=abc123",
            "published",
            DateTimeOffset.Parse("2026-03-28T10:00:00+00:00"));

        Assert.Collection(
            row.Values!,
            cell => Assert.Equal("2026-03-27T08:15:30Z", cell.UserEnteredValue?.StringValue),
            cell => Assert.Equal("My Channel", cell.UserEnteredValue?.StringValue),
            cell => Assert.Equal("source.mp4", cell.UserEnteredValue?.StringValue),
            cell => Assert.Equal("My title", cell.UserEnteredValue?.StringValue),
            cell => Assert.Equal("abc123", cell.UserEnteredValue?.StringValue),
            cell =>
            {
                Assert.Equal("=HYPERLINK(\"https://youtube.test/watch?v=abc123\",\"Відкрити\")", cell.UserEnteredValue?.FormulaValue);
            },
            cell => Assert.Equal("published", cell.UserEnteredValue?.StringValue),
            cell => Assert.Equal("2026-03-28T10:00:00Z", cell.UserEnteredValue?.StringValue),
            cell => Assert.Equal(string.Empty, cell.UserEnteredValue?.StringValue),
            cell => Assert.Equal(string.Empty, cell.UserEnteredValue?.StringValue));

        Assert.Equal(10, row.Values!.Count);
    }

    [Fact]
    public void BuildNormalizationRequests_DefinesHeadersWidthsAndStatusRules()
    {
        var requests = GoogleSheetsAuditSheet.BuildNormalizationRequests(sheetId: 42, existingConditionalFormatRuleCount: 2);

        Assert.Equal(1, requests.Count(r => r.ClearBasicFilter is not null));
        Assert.Equal(1, requests.Count(r => r.UpdateSheetProperties is not null));
        Assert.Equal(1, requests.Count(r => r.AddBanding is not null));
        Assert.Equal(1, requests.Count(r => r.UpdateCells is not null));
        Assert.Equal(3, requests.Count(r => r.RepeatCell is not null));
        Assert.Equal(1, requests.Count(r => r.SetDataValidation is not null));
        Assert.Equal(1, requests.Count(r => r.SetBasicFilter is not null));
        Assert.Equal(GoogleSheetsAuditSheet.ColumnCount + 1, requests.Count(r => r.UpdateDimensionProperties is not null));
        Assert.Equal(2, requests.Count(r => r.DeleteConditionalFormatRule is not null));
        Assert.Equal(3, requests.Count(r => r.AddConditionalFormatRule is not null));

        var headerRequest = Assert.Single(requests, r => r.UpdateCells is not null).UpdateCells!;
        var headerValues = headerRequest.Rows!.Single().Values!.Select(cell => cell.UserEnteredValue?.StringValue ?? string.Empty).ToArray();
        Assert.Equal(GoogleSheetsAuditSheet.HeaderDisplayValues, headerValues);

        var statusRules = requests.Where(r => r.AddConditionalFormatRule is not null).Select(r => r.AddConditionalFormatRule!.Rule!).ToArray();
        Assert.Contains(statusRules, rule => HasStatusRule(rule, "published", 0.86, 0.94, 0.86));
        Assert.Contains(statusRules, rule => HasStatusRule(rule, "scheduled", 0.85, 0.91, 0.98));
        Assert.Contains(statusRules, rule => HasStatusRule(rule, "failed", 0.98, 0.87, 0.87));
    }

    [Fact]
    public void BuildPreNormalizationRequests_WhenLegacyHeader_InsertsChannelColumn()
    {
        var requests = GoogleSheetsAuditSheet.BuildPreNormalizationRequests(
            sheetId: 42,
            headerKind: GoogleSheetsAuditSheet.HeaderKind.LegacyWithoutChannel,
            hasAnyFirstRowValues: true,
            existingColumnCount: 7);

        Assert.Contains(requests, r => r.InsertDimension?.Range?.Dimension == "COLUMNS" && r.InsertDimension.Range.StartIndex == 1);
    }

    [Fact]
    public void BuildPreNormalizationRequests_WhenFirstRowIsData_InsertsHeaderRowAndChannelColumn()
    {
        var requests = GoogleSheetsAuditSheet.BuildPreNormalizationRequests(
            sheetId: 42,
            headerKind: GoogleSheetsAuditSheet.HeaderKind.DataOrMissing,
            hasAnyFirstRowValues: true,
            existingColumnCount: 7);

        Assert.Contains(requests, r => r.InsertDimension?.Range?.Dimension == "ROWS" && r.InsertDimension.Range.StartIndex == 0);
        Assert.Contains(requests, r => r.InsertDimension?.Range?.Dimension == "COLUMNS" && r.InsertDimension.Range.StartIndex == 1);
    }

    private static bool HasStatusRule(ConditionalFormatRule rule, string expectedStatus, double red, double green, double blue)
    {
        var condition = rule.BooleanRule?.Condition;
        var color = rule.BooleanRule?.Format?.BackgroundColor;

        return condition?.Type == "TEXT_EQ"
            && condition.Values?.SingleOrDefault()?.UserEnteredValue == expectedStatus
            && color is not null
            && Math.Abs(color.Red.GetValueOrDefault() - red) < 0.001
            && Math.Abs(color.Green.GetValueOrDefault() - green) < 0.001
            && Math.Abs(color.Blue.GetValueOrDefault() - blue) < 0.001;
    }
}
