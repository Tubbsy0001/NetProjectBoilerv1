namespace SoapCountryApp.Models;

public record WsdlParseRequest(
    string? PrimarySource,
    IReadOnlyList<string> AdditionalSources,
    bool FollowImports);

public record WsdlParseResult(
    IReadOnlyList<WsdlOperationDescriptor> Operations,
    IReadOnlyList<string> Sources);
