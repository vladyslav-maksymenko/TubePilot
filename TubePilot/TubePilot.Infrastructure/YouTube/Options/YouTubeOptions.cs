namespace TubePilot.Infrastructure.YouTube.Options;

public sealed record YouTubeOptions
{
    public const string SectionName = "YouTube";

    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? RefreshToken { get; init; }

    public string DefaultCategoryId { get; init; } = "22";
    public int MaxUploadsPerDay { get; init; } = 6;
}
