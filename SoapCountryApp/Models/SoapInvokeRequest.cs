namespace SoapCountryApp.Models;

public class SoapInvokeRequest
{
    public string? EndpointUrl { get; set; }
    public string? SoapAction { get; set; }
    public string? Payload { get; set; }
}

public record SoapInvokeResponse(
    int StatusCode,
    string ResponseBody,
    IReadOnlyDictionary<string, IEnumerable<string>> Headers,
    string? Error);
