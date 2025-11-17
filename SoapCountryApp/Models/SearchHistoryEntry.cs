namespace SoapCountryApp.Models;

public class SearchHistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? PrimarySource { get; init; }
    public List<string> AdditionalSources { get; init; } = new();
    public bool FollowImports { get; init; }
    public DateTime SavedAt { get; init; } = DateTime.UtcNow;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(PrimarySource)
            ? "(Unnamed search)"
            : PrimarySource!;

    public override bool Equals(object? obj)
    {
        if (obj is not SearchHistoryEntry other)
        {
            return false;
        }

        return string.Equals(Normalize(PrimarySource), Normalize(other.PrimarySource), StringComparison.OrdinalIgnoreCase)
               && FollowImports == other.FollowImports
               && NormalizeSources(AdditionalSources!)
                   .SequenceEqual(NormalizeSources(other.AdditionalSources!), StringComparer.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Normalize(PrimarySource), StringComparer.OrdinalIgnoreCase);
        hash.Add(FollowImports);
        foreach (var src in NormalizeSources(AdditionalSources!))
        {
            hash.Add(src, StringComparer.OrdinalIgnoreCase);
        }
        return hash.ToHashCode();
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static IEnumerable<string> NormalizeSources(IEnumerable<string> sources) =>
        sources
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
}
