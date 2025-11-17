namespace SoapCountryApp.Models;

public record WsdlParameterDescriptor(
    string Name,
    string? TypeName,
    bool IsArray,
    string SampleXml,
    string? Description,
    string? ValueDescription,
    string? ExampleValue,
    IReadOnlyList<string> AllowedValues);

public record WsdlOperationDescriptor(
    string Name,
    string SoapAction,
    string InputMessage,
    string OutputMessage,
    string Documentation,
    string SampleEnvelope,
    IReadOnlyList<WsdlParameterDescriptor> Parameters,
    string Source);
