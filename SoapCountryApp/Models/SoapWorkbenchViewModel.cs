using System.ComponentModel.DataAnnotations;

namespace SoapCountryApp.Models;

public class SoapWorkbenchViewModel
{
    [Display(Name = "Primary WSDL URL")]
    public string? WsdlUrl { get; set; }

    [Display(Name = "Additional descriptor URLs (one per line)")]
    public string? AdditionalSources { get; set; }

    [Display(Name = "Follow wsdl/xsd imports automatically")]
    public bool FollowImports { get; set; } = true;

    [Display(Name = "Execution endpoint URL (for running payloads)")]
    public string? ExecutionEndpoint { get; set; }

    public List<WsdlOperationDescriptor> Operations { get; set; } = new();

    public List<string> LoadedSources { get; set; } = new();

    public List<SearchHistoryEntry> SavedSearches { get; set; } = new();

    public Guid? ActiveHistoryId { get; set; }

    public string? Error { get; set; }

    public DateTime? ParsedAt { get; set; }

    public bool HasOperations => Operations.Count > 0;

    public int SourceCount => LoadedSources.Count;

    public IReadOnlyList<string> AdditionalSourceList =>
        (AdditionalSources ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
