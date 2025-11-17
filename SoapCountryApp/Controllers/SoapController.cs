using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Net.Http;
using SoapCountryApp.Models;
using SoapCountryApp.Services;

namespace SoapCountryApp.Controllers;

public class SoapController : Controller
{
    private readonly WsdlParser _wsdlParser;
    private readonly SearchHistoryStore _historyStore;
    private readonly IHttpClientFactory _httpClientFactory;

    public SoapController(WsdlParser wsdlParser, SearchHistoryStore historyStore, IHttpClientFactory httpClientFactory)
    {
        _wsdlParser = wsdlParser;
        _historyStore = historyStore;
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Index(Guid? id, CancellationToken cancellationToken)
    {
        var saved = (await _historyStore.GetAllAsync(cancellationToken)).ToList();
        var model = new SoapWorkbenchViewModel
        {
            SavedSearches = saved
        };

        if (id.HasValue)
        {
            var entry = saved.FirstOrDefault(s => s.Id == id.Value);
            if (entry != null)
            {
                model.ActiveHistoryId = entry.Id;
                model.WsdlUrl = entry.PrimarySource;
                model.AdditionalSources = string.Join(Environment.NewLine, entry.AdditionalSources);
                model.FollowImports = entry.FollowImports;
            }
        }

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Describe(SoapWorkbenchViewModel model, CancellationToken cancellationToken)
    {
        model ??= new SoapWorkbenchViewModel();
        model.Operations = new();
        model.LoadedSources = new();
        model.SavedSearches = (await _historyStore.GetAllAsync(cancellationToken)).ToList();

        if (string.IsNullOrWhiteSpace(model.WsdlUrl) && !model.AdditionalSourceList.Any())
        {
            model.Error = "Provide at least one WSDL or descriptor URL.";
            return View("Index", model);
        }

        try
        {
            var request = new WsdlParseRequest(
                model.WsdlUrl,
                model.AdditionalSourceList,
                model.FollowImports);

            var result = await _wsdlParser.ParseAsync(request, cancellationToken);
            var deduped = result.Operations
                .GroupBy(op => $"{op.Source}|{op.Name}|{op.SoapAction}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(op => op.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            model.Operations = deduped;
            model.LoadedSources = result.Sources.ToList();
            model.Error = model.Operations.Count == 0
                ? "No operations were discovered in the provided sources."
                : null;
            model.ParsedAt = model.Operations.Count > 0 ? DateTime.UtcNow : null;

            if (model.Operations.Count > 0)
            {
                await _historyStore.SaveAsync(new SearchHistoryEntry
                {
                    PrimarySource = model.WsdlUrl,
                    AdditionalSources = model.AdditionalSourceList.ToList(),
                    FollowImports = model.FollowImports
                }, cancellationToken);

                model.SavedSearches = (await _historyStore.GetAllAsync(cancellationToken)).ToList();
            }
        }
        catch (Exception ex)
        {
            model.Error = $"Failed to parse descriptors: {ex.Message}";
        }

        return View("Index", model);
    }

    [HttpPost("invoke")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Invoke([FromBody] SoapInvokeRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.EndpointUrl) || string.IsNullOrWhiteSpace(request.Payload))
        {
            return BadRequest(new SoapInvokeResponse(0, string.Empty, new Dictionary<string, IEnumerable<string>>(), "Endpoint URL and payload are required."));
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, request.EndpointUrl);
            httpRequest.Content = new StringContent(request.Payload, Encoding.UTF8, "text/xml");
            httpRequest.Headers.Add("Accept", "text/xml");
            if (!string.IsNullOrWhiteSpace(request.SoapAction))
            {
                httpRequest.Headers.TryAddWithoutValidation("SOAPAction", request.SoapAction);
            }

            using var response = await client.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var headers = MergeHeaders(response);
            var payload = new SoapInvokeResponse((int)response.StatusCode, body, headers, null);
            return Ok(payload);
        }
        catch (Exception ex)
        {
            return Ok(new SoapInvokeResponse(0, string.Empty, new Dictionary<string, IEnumerable<string>>(), ex.Message));
        }
    }

    private static IReadOnlyDictionary<string, IEnumerable<string>> MergeHeaders(HttpResponseMessage response)
    {
        var dict = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            dict[header.Key] = header.Value;
        }
        foreach (var header in response.Content.Headers)
        {
            dict[header.Key] = header.Value;
        }
        return dict;
    }
}
