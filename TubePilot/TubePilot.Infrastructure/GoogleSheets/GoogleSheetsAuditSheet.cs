using System.Globalization;
using Google.Apis.Sheets.v4.Data;

namespace TubePilot.Infrastructure.GoogleSheets;

internal static class GoogleSheetsAuditSheet
{
    internal const int ColumnCount = 10;

    internal enum HeaderKind
    {
        Expected = 0,
        LegacyWithoutChannel = 1,
        DataOrMissing = 2
    }

    internal static readonly IReadOnlyList<string> HeaderKeys =
    [
        "ts_utc",
        "channel",
        "source_file",
        "title",
        "youtube_id",
        "youtube_url",
        "status",
        "scheduled_at_utc",
        "quota_used",
        "notes"
    ];

    internal static readonly IReadOnlyList<string> HeaderDisplayValues =
    [
        "Час (UTC)",
        "Канал",
        "Файл (source)",
        "Заголовок (title)",
        "YouTube ID",
        "YouTube URL",
        "Статус",
        "Заплановано (UTC)",
        "Квота",
        "Нотатки"
    ];

    private static readonly int[] ColumnWidths =
    [
        210,
        160,
        220,
        320,
        150,
        360,
        130,
        220,
        120,
        320
    ];

    private static readonly Color HeaderBackground = new()
    {
        Red = 0.94f,
        Green = 0.95f,
        Blue = 0.96f
    };

    private static readonly Color HeaderForeground = new()
    {
        Red = 0.12f,
        Green = 0.12f,
        Blue = 0.12f
    };

    private static readonly Color BandedRowColor1 = new()
    {
        Red = 0.98f,
        Green = 0.98f,
        Blue = 0.98f
    };

    private static readonly Color BandedRowColor2 = new()
    {
        Red = 1.00f,
        Green = 1.00f,
        Blue = 1.00f
    };

    private static readonly Color PublishedBackground = new()
    {
        Red = 0.86f,
        Green = 0.94f,
        Blue = 0.86f
    };

    private static readonly Color ScheduledBackground = new()
    {
        Red = 0.85f,
        Green = 0.91f,
        Blue = 0.98f
    };

    private static readonly Color FailedBackground = new()
    {
        Red = 0.98f,
        Green = 0.87f,
        Blue = 0.87f
    };

    private const string UtcTimestampFormat = "yyyy-MM-ddTHH:mm:ss'Z'";

    internal static HeaderKind AnalyzeHeaderRow(IList<object>? row)
    {
        if (row is null || row.Count == 0)
        {
            return HeaderKind.DataOrMissing;
        }

        var normalized = row
            .Select(static value => (value?.ToString() ?? string.Empty).Trim().ToLowerInvariant())
            .ToArray();

        if (normalized.All(static value => string.IsNullOrWhiteSpace(value)))
        {
            return HeaderKind.DataOrMissing;
        }

        if (normalized.Length == HeaderKeys.Count &&
            normalized.SequenceEqual(HeaderKeys.Select(static header => header.ToLowerInvariant())))
        {
            return HeaderKind.Expected;
        }

        if (normalized.Length == HeaderDisplayValues.Count &&
            normalized.SequenceEqual(HeaderDisplayValues.Select(static header => header.ToLowerInvariant())))
        {
            return HeaderKind.Expected;
        }

        if (normalized.Length >= 7 &&
            normalized[0] == "ts_utc" &&
            normalized[1] == "source_file" &&
            normalized[2] == "title" &&
            normalized[3] == "youtube_id" &&
            normalized[4] == "youtube_url" &&
            normalized[5] == "status")
        {
            return HeaderKind.LegacyWithoutChannel;
        }

        return HeaderKind.DataOrMissing;
    }

    internal static bool HasAnyValues(IList<object>? row)
        => row is not null &&
           row.Any(static value => !string.IsNullOrWhiteSpace(value?.ToString()));

    internal static IReadOnlyList<Request> BuildPreNormalizationRequests(int sheetId, HeaderKind headerKind, bool hasAnyFirstRowValues, int existingColumnCount)
    {
        var requests = new List<Request>();

        var shouldInsertHeaderRow = headerKind == HeaderKind.DataOrMissing && hasAnyFirstRowValues;
        var shouldInsertChannelColumn = headerKind == HeaderKind.LegacyWithoutChannel || shouldInsertHeaderRow;

        if (shouldInsertHeaderRow)
        {
            requests.Add(new Request
            {
                InsertDimension = new InsertDimensionRequest
                {
                    Range = new DimensionRange
                    {
                        SheetId = sheetId,
                        Dimension = "ROWS",
                        StartIndex = 0,
                        EndIndex = 1
                    },
                    InheritFromBefore = false
                }
            });
        }

        if (shouldInsertChannelColumn)
        {
            requests.Add(new Request
            {
                InsertDimension = new InsertDimensionRequest
                {
                    Range = new DimensionRange
                    {
                        SheetId = sheetId,
                        Dimension = "COLUMNS",
                        StartIndex = 1,
                        EndIndex = 2
                    },
                    InheritFromBefore = false
                }
            });
        }

        var effectiveColumnCount = existingColumnCount + (shouldInsertChannelColumn ? 1 : 0);
        if (effectiveColumnCount < ColumnCount)
        {
            requests.Add(new Request
            {
                AppendDimension = new AppendDimensionRequest
                {
                    SheetId = sheetId,
                    Dimension = "COLUMNS",
                    Length = ColumnCount - effectiveColumnCount
                }
            });
        }

        return requests;
    }

    internal static RowData BuildAuditRow(
        DateTimeOffset timestampUtc,
        string channel,
        string sourceFile,
        string title,
        string youtubeId,
        string youtubeUrl,
        string status,
        DateTimeOffset? scheduledAtUtc)
    {
        return new RowData
        {
            Values =
            [
                TextCell(FormatUtc(timestampUtc)),
                TextCell(channel),
                TextCell(sourceFile),
                TextCell(title),
                TextCell(youtubeId),
                FormulaCell(BuildHyperlinkFormula(youtubeUrl)),
                TextCell(status),
                TextCell(FormatUtc(scheduledAtUtc)),
                TextCell(string.Empty),
                TextCell(string.Empty)
            ]
        };
    }

    internal static IReadOnlyList<Request> BuildNormalizationRequests(int sheetId, int existingConditionalFormatRuleCount)
    {
        var requests = new List<Request>
        {
            new()
            {
                ClearBasicFilter = new ClearBasicFilterRequest
                {
                    SheetId = sheetId
                }
            },
            new()
            {
                AddBanding = new AddBandingRequest
                {
                    BandedRange = new BandedRange
                    {
                        Range = new GridRange
                        {
                            SheetId = sheetId,
                            StartRowIndex = 0,
                            StartColumnIndex = 0,
                            EndColumnIndex = ColumnCount
                        },
                        RowProperties = new BandingProperties
                        {
                            FirstBandColor = BandedRowColor1,
                            SecondBandColor = BandedRowColor2,
                            HeaderColor = HeaderBackground
                        }
                    }
                }
            },
            new()
            {
                UpdateSheetProperties = new UpdateSheetPropertiesRequest
                {
                    Properties = new SheetProperties
                    {
                        SheetId = sheetId,
                        GridProperties = new GridProperties
                        {
                            FrozenRowCount = 1
                        }
                    },
                    Fields = "gridProperties.frozenRowCount"
                }
            },
            new()
            {
                UpdateDimensionProperties = new UpdateDimensionPropertiesRequest
                {
                    Range = new DimensionRange
                    {
                        SheetId = sheetId,
                        Dimension = "ROWS",
                        StartIndex = 0,
                        EndIndex = 1
                    },
                    Properties = new DimensionProperties
                    {
                        PixelSize = 44
                    },
                    Fields = "pixelSize"
                }
            },
            new()
            {
                UpdateCells = new UpdateCellsRequest
                {
                    Start = new GridCoordinate
                    {
                        SheetId = sheetId,
                        RowIndex = 0,
                        ColumnIndex = 0
                    },
                    Rows =
                    [
                        new RowData
                        {
                            Values = HeaderDisplayValues.Select(TextCell).ToList()
                        }
                    ],
                    Fields = "userEnteredValue"
                }
            },
            new()
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = new GridRange
                    {
                        SheetId = sheetId,
                        StartRowIndex = 0,
                        EndRowIndex = 1,
                        StartColumnIndex = 0,
                        EndColumnIndex = ColumnCount
                    },
                    Cell = new CellData
                    {
                        UserEnteredFormat = new CellFormat
                        {
                            BackgroundColor = HeaderBackground,
                            HorizontalAlignment = "CENTER",
                            VerticalAlignment = "MIDDLE",
                            TextFormat = new TextFormat
                            {
                                Bold = true,
                                ForegroundColor = HeaderForeground
                            }
                        }
                    },
                    Fields = "userEnteredFormat"
                }
            },
            new()
            {
                SetDataValidation = new SetDataValidationRequest
                {
                    Range = new GridRange
                    {
                        SheetId = sheetId,
                        StartRowIndex = 1,
                        StartColumnIndex = 6,
                        EndColumnIndex = 7
                    },
                    Rule = new DataValidationRule
                    {
                        Condition = new BooleanCondition
                        {
                            Type = "ONE_OF_LIST",
                            Values =
                            [
                                new ConditionValue { UserEnteredValue = "published" },
                                new ConditionValue { UserEnteredValue = "scheduled" },
                                new ConditionValue { UserEnteredValue = "failed" }
                            ]
                        },
                        Strict = true,
                        ShowCustomUi = true
                    }
                }
            },
            new()
            {
                SetBasicFilter = new SetBasicFilterRequest
                {
                    Filter = new BasicFilter
                    {
                        Range = new GridRange
                        {
                            SheetId = sheetId,
                            StartRowIndex = 0,
                            StartColumnIndex = 0,
                            EndColumnIndex = ColumnCount
                        }
                    }
                }
            }
        };

        for (var columnIndex = 0; columnIndex < ColumnWidths.Length; columnIndex++)
        {
            requests.Add(new Request
            {
                UpdateDimensionProperties = new UpdateDimensionPropertiesRequest
                {
                    Range = new DimensionRange
                    {
                        SheetId = sheetId,
                        Dimension = "COLUMNS",
                        StartIndex = columnIndex,
                        EndIndex = columnIndex + 1
                    },
                    Properties = new DimensionProperties
                    {
                        PixelSize = ColumnWidths[columnIndex]
                    },
                    Fields = "pixelSize"
                }
            });
        }

        requests.AddRange(
            new[]
            {
                TextWrapRequest(sheetId, 3),
                TextWrapRequest(sheetId, 9)
            });

        for (var index = existingConditionalFormatRuleCount - 1; index >= 0; index--)
        {
            requests.Add(new Request
            {
                DeleteConditionalFormatRule = new DeleteConditionalFormatRuleRequest
                {
                    SheetId = sheetId,
                    Index = index
                }
            });
        }

        requests.Add(AddStatusRule(sheetId, "published", PublishedBackground, 0));
        requests.Add(AddStatusRule(sheetId, "scheduled", ScheduledBackground, 1));
        requests.Add(AddStatusRule(sheetId, "failed", FailedBackground, 2));

        return requests;
    }

    private static Request TextWrapRequest(int sheetId, int columnIndex)
        => new()
        {
            RepeatCell = new RepeatCellRequest
            {
                Range = new GridRange
                {
                    SheetId = sheetId,
                    StartRowIndex = 1,
                    StartColumnIndex = columnIndex,
                    EndColumnIndex = columnIndex + 1
                },
                Cell = new CellData
                {
                    UserEnteredFormat = new CellFormat
                    {
                        WrapStrategy = "WRAP"
                    }
                },
                Fields = "userEnteredFormat.wrapStrategy"
            }
        };

    private static Request AddStatusRule(int sheetId, string status, Color background, int index)
        => new()
        {
            AddConditionalFormatRule = new AddConditionalFormatRuleRequest
            {
                Index = index,
                Rule = new ConditionalFormatRule
                {
                    Ranges =
                    [
                        new GridRange
                        {
                            SheetId = sheetId,
                            StartRowIndex = 1,
                            StartColumnIndex = 6,
                            EndColumnIndex = 7
                        }
                    ],
                    BooleanRule = new BooleanRule
                    {
                        Condition = new BooleanCondition
                        {
                            Type = "TEXT_EQ",
                            Values =
                            [
                                new ConditionValue
                                {
                                    UserEnteredValue = status
                                }
                            ]
                        },
                        Format = new CellFormat
                        {
                            BackgroundColor = background
                        }
                    }
                }
            }
        };

    private static CellData TextCell(string? value)
        => new()
        {
            UserEnteredValue = new ExtendedValue
            {
                StringValue = value ?? string.Empty
            }
        };

    private static CellData FormulaCell(string? formula)
        => string.IsNullOrWhiteSpace(formula)
            ? TextCell(string.Empty)
            : new CellData
            {
                UserEnteredValue = new ExtendedValue
                {
                    FormulaValue = formula
                }
            };

    private static string FormatUtc(DateTimeOffset? value)
        => value?.UtcDateTime.ToString(UtcTimestampFormat, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string BuildHyperlinkFormula(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var escaped = EscapeFormulaText(url);
        return $"=HYPERLINK(\"{escaped}\",\"Відкрити\")";
    }

    private static string EscapeFormulaText(string value)
        => value.Replace("\"", "\"\"", StringComparison.Ordinal);
}
