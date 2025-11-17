using System.Text.Json;
using SoapCountryApp.Models;

namespace SoapCountryApp.Services;

public class SearchHistoryStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public SearchHistoryStore(IHostEnvironment environment)
    {
        var dataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "searchHistory.json");
    }

    public async Task<IReadOnlyList<SearchHistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<SearchHistoryEntry>();
            }

            await using var stream = File.OpenRead(_filePath);
            var entries = await JsonSerializer.DeserializeAsync<List<SearchHistoryEntry>>(stream, _serializerOptions, cancellationToken)
                          ?? new List<SearchHistoryEntry>();

            return entries
                .OrderByDescending(e => e.SavedAt)
                .ToList();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SaveAsync(SearchHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var entries = new List<SearchHistoryEntry>();
            if (File.Exists(_filePath))
            {
                await using var readStream = File.OpenRead(_filePath);
                entries = await JsonSerializer.DeserializeAsync<List<SearchHistoryEntry>>(readStream, _serializerOptions, cancellationToken)
                          ?? new List<SearchHistoryEntry>();
            }

            var normalizedEntry = new SearchHistoryEntry
            {
                PrimarySource = entry.PrimarySource?.Trim(),
                AdditionalSources = entry.AdditionalSources
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                FollowImports = entry.FollowImports,
                ExecutionEndpoint = entry.ExecutionEndpoint?.Trim(),
                SavedAt = DateTime.UtcNow
            };

            var existing = entries.FirstOrDefault(e => e.Equals(normalizedEntry));
            if (existing != null)
            {
                entries.Remove(existing);
                normalizedEntry = new SearchHistoryEntry
                {
                    Id = existing.Id,
                    PrimarySource = normalizedEntry.PrimarySource,
                    AdditionalSources = normalizedEntry.AdditionalSources,
                    FollowImports = normalizedEntry.FollowImports,
                    ExecutionEndpoint = normalizedEntry.ExecutionEndpoint,
                    SavedAt = DateTime.UtcNow
                };
            }

            entries.Add(normalizedEntry);

            await using var writeStream = File.Create(_filePath);
            await JsonSerializer.SerializeAsync(writeStream, entries, _serializerOptions, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }
}
