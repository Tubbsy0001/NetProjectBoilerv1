using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SoapCountryApp.Models;

namespace SoapCountryApp.Services;

public class WsdlParser
{
    private static readonly XNamespace WsdlNs = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace SoapNs = "http://schemas.xmlsoap.org/wsdl/soap/";
    private static readonly XNamespace XsdNs = "http://www.w3.org/2001/XMLSchema";

    private readonly HttpClient _httpClient;

    public WsdlParser(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<WsdlParseResult> ParseAsync(WsdlParseRequest request, CancellationToken cancellationToken = default)
    {
        var loader = new DocumentLoader(_httpClient);
        var documents = await loader.LoadAsync(request, cancellationToken);

        var wsdlDocuments = documents.Where(d => d.Document.Root?.Name.LocalName.Equals("definitions", StringComparison.OrdinalIgnoreCase) == true).ToList();
        var manifestDocuments = documents.Except(wsdlDocuments).ToList();

        var messageMap = new Dictionary<string, XElement>();
        var schemaContext = new SchemaContext();
        var portOperations = new Dictionary<string, PortOperation>();

        foreach (var doc in wsdlDocuments)
        {
            MergeMessages(doc.Document, messageMap);
            MergeSchemas(doc.Document, schemaContext);
            MergePortOperations(doc.Document, portOperations, doc);
        }

        var operations = new List<WsdlOperationDescriptor>();

        foreach (var doc in wsdlDocuments)
        {
            operations.AddRange(ParseWsdlBindings(doc, portOperations, messageMap, schemaContext));
        }

        foreach (var doc in manifestDocuments)
        {
            operations.AddRange(ParseExecutableManifest(doc));
        }

        return new WsdlParseResult(operations, documents.Select(d => d.Source.AbsoluteUri).ToList());
    }

    private static void MergeMessages(XDocument document, IDictionary<string, XElement> map)
    {
        foreach (var message in document.Descendants(WsdlNs + "message"))
        {
            var name = message.Attribute("name")?.Value;
            if (!string.IsNullOrWhiteSpace(name))
            {
                map[name] = message;
            }
        }
    }

    private static void MergeSchemas(XDocument document, SchemaContext context)
    {
        foreach (var schema in document.Descendants(XsdNs + "schema"))
        {
            var targetNs = schema.Attribute("targetNamespace")?.Value ?? document.Root?.Attribute("targetNamespace")?.Value ?? string.Empty;

            foreach (var element in schema.Elements(XsdNs + "element"))
            {
                var name = element.Attribute("name")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    context.Elements[XName.Get(name, targetNs)] = element;
                }
            }

            foreach (var complexType in schema.Elements(XsdNs + "complexType"))
            {
                var name = complexType.Attribute("name")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    context.ComplexTypes[XName.Get(name, targetNs)] = complexType;
                }
            }

            foreach (var simpleType in schema.Elements(XsdNs + "simpleType"))
            {
                var name = simpleType.Attribute("name")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    context.SimpleTypes[XName.Get(name, targetNs)] = simpleType;
                }
            }
        }
    }

    private static void MergePortOperations(XDocument document, IDictionary<string, PortOperation> map, WsdlDocument doc)
    {
        foreach (var op in document.Descendants(WsdlNs + "portType").Elements(WsdlNs + "operation"))
        {
            var name = op.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            map[name] = new PortOperation(
                name,
                op.Element(WsdlNs + "input")?.Attribute("message")?.Value,
                op.Element(WsdlNs + "output")?.Attribute("message")?.Value,
                op.Element(WsdlNs + "documentation")?.Value,
                doc);
        }
    }

    private static IReadOnlyList<WsdlOperationDescriptor> ParseWsdlBindings(
        WsdlDocument doc,
        IReadOnlyDictionary<string, PortOperation> portOperations,
        IReadOnlyDictionary<string, XElement> messageMap,
        SchemaContext schemaContext)
    {
        var targetNamespace = doc.Document.Root?.Attribute("targetNamespace")?.Value ?? "urn:soap";
        var operations = new List<WsdlOperationDescriptor>();

        foreach (var binding in doc.Document.Descendants(WsdlNs + "binding"))
        {
            foreach (var operation in binding.Elements(WsdlNs + "operation"))
            {
                var operationName = operation.Attribute("name")?.Value;
                if (string.IsNullOrWhiteSpace(operationName))
                {
                    continue;
                }

                portOperations.TryGetValue(operationName, out var portOp);

                var soapAction = operation
                    .Elements(SoapNs + "operation")
                    .FirstOrDefault()
                    ?.Attribute("soapAction")
                    ?.Value ?? string.Empty;

                var inputMessage = ExtractLocalName(portOp?.InputMessage);
                var outputMessage = ExtractLocalName(portOp?.OutputMessage);
                var documentation = portOp?.Documentation ?? string.Empty;

                var parameters = DescribeParameters(inputMessage, messageMap, schemaContext, targetNamespace);
                var sampleEnvelope = BuildSampleEnvelope(targetNamespace, operationName, parameters);

                operations.Add(new WsdlOperationDescriptor(
                    operationName,
                    soapAction,
                    inputMessage,
                    outputMessage,
                    documentation,
                    sampleEnvelope,
                    parameters,
                    doc.Source.AbsoluteUri));
            }
        }

        return operations;
    }

    private static IReadOnlyList<WsdlParameterDescriptor> DescribeParameters(
        string? messageName,
        IReadOnlyDictionary<string, XElement> messageMap,
        SchemaContext schemaContext,
        string targetNamespace)
    {
        if (string.IsNullOrWhiteSpace(messageName) || !messageMap.TryGetValue(messageName, out var message))
        {
            return Array.Empty<WsdlParameterDescriptor>();
        }

        var descriptors = new List<WsdlParameterDescriptor>();

        foreach (var part in message.Elements(WsdlNs + "part"))
        {
            var elementReference = part.Attribute("element")?.Value;
            var typeReference = part.Attribute("type")?.Value;
            var partName = part.Attribute("name")?.Value ?? ExtractLocalName(elementReference) ?? "parameter";

            var resolvedElement = ResolveQualifiedName(part, elementReference);
            var resolvedType = ResolveQualifiedName(part, typeReference);

            bool isArray = false;
            string? typeName = resolvedType?.ToString() ?? resolvedElement?.ToString();
            string sampleXml;
            string? description = GetDocumentation(part);
            XElement? elementDefinition = null;

            if (resolvedElement != null)
            {
                schemaContext.Elements.TryGetValue(resolvedElement, out elementDefinition);
            }

            if (elementDefinition != null)
            {
                isArray = IsArray(elementDefinition);
                typeName = elementDefinition.Attribute("type")?.Value ?? typeName;
                sampleXml = BuildElementSample(elementDefinition, schemaContext, 0);
                description ??= GetDocumentation(elementDefinition);
            }
            else if (resolvedType != null && schemaContext.ComplexTypes.TryGetValue(resolvedType, out var complexType))
            {
                sampleXml = BuildComplexTypeSample(partName, complexType, schemaContext, 0);
            }
            else
            {
                sampleXml = $"<tns:{partName}>{partName}Value</tns:{partName}>";
            }

            var metadata = DescribeValueMetadata(elementDefinition, schemaContext, resolvedType);
            var decoratedSample = DecorateSample(sampleXml, metadata);
            var exampleValue = metadata.Example ?? metadata.AllowedValues.FirstOrDefault();
            descriptors.Add(new WsdlParameterDescriptor(
                partName,
                typeName,
                isArray,
                decoratedSample.Trim(),
                description,
                metadata.Description,
                exampleValue,
                metadata.AllowedValues));
        }

        return descriptors;
    }

    private static IReadOnlyList<WsdlOperationDescriptor> ParseExecutableManifest(WsdlDocument doc)
    {
        var root = doc.Document.Root;
        if (root == null)
        {
            return Array.Empty<WsdlOperationDescriptor>();
        }

        var functions = root
            .Descendants()
            .Where(x => x.Name.LocalName.Equals("Function", StringComparison.OrdinalIgnoreCase)
                        || x.Name.LocalName.Equals("ExecutableFunction", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (root.Name.LocalName.Contains("Executable", StringComparison.OrdinalIgnoreCase) && !functions.Any())
        {
            functions = root.Elements().ToList();
        }

        if (!functions.Any())
        {
            return Array.Empty<WsdlOperationDescriptor>();
        }

        var operations = new List<WsdlOperationDescriptor>();

        foreach (var function in functions)
        {
            var name = function.Attribute("name")?.Value
                       ?? function.Element("Name")?.Value
                       ?? "Function";

            var soapAction = function.Attribute("soapAction")?.Value
                             ?? function.Attribute("action")?.Value
                             ?? function.Element("SoapAction")?.Value
                             ?? string.Empty;

            var documentation = function.Attribute("description")?.Value
                                ?? function.Element("Description")?.Value
                                ?? function.Element("Documentation")?.Value
                                ?? string.Empty;

            var sampleEnvelope = function.Element("Envelope")?.Value
                                 ?? function.Element("Template")?.Value
                                 ?? function.Element("Sample")?.Value
                                 ?? "<!-- Provide the raw XML payload for this function -->";

            var parameters = new List<WsdlParameterDescriptor>();
            foreach (var parameter in function.Elements().Where(e => e.Name.LocalName.Equals("Parameter", StringComparison.OrdinalIgnoreCase)))
            {
                var paramName = parameter.Attribute("name")?.Value
                                ?? parameter.Element("Name")?.Value
                                ?? "Parameter";
                var typeName = parameter.Attribute("type")?.Value
                               ?? parameter.Element("Type")?.Value;
                var isArray = bool.TryParse(parameter.Attribute("repeating")?.Value, out var repeating) && repeating;
                var sample = parameter.Element("Sample")?.Value
                             ?? $"<{paramName}>{paramName}Value</{paramName}>";
                var description = parameter.Attribute("description")?.Value
                                  ?? parameter.Element("Description")?.Value;

                var manifestHint = BuildManifestValueHint(parameter, typeName);
                var manifestExample = parameter.Element("Example")?.Value
                                      ?? parameter.Attribute("example")?.Value;
                var allowedValues = parameter
                    .Elements("AllowedValue")
                    .Select(e => e.Value)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .ToList();

                var metadata = new ValueMetadata(manifestHint, manifestExample, allowedValues);
                var decoratedSample = DecorateSample(sample, metadata);
                parameters.Add(new WsdlParameterDescriptor(
                    paramName,
                    typeName,
                    isArray,
                    decoratedSample,
                    description,
                    manifestHint,
                    manifestExample ?? allowedValues.FirstOrDefault(),
                    allowedValues));
            }

            operations.Add(new WsdlOperationDescriptor(
                name,
                soapAction,
                string.Empty,
                string.Empty,
                documentation,
                sampleEnvelope,
                parameters,
                doc.Source.AbsoluteUri));
        }

        return operations;
    }

    private static string BuildSampleEnvelope(string targetNamespace, string operationName, IReadOnlyList<WsdlParameterDescriptor> parameters)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
        sb.AppendLine($@"<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:tns=""{targetNamespace}"">");
        sb.AppendLine("  <soap:Header />");
        sb.AppendLine("  <soap:Body>");
        sb.AppendLine($"    <tns:{operationName}>");

        if (parameters.Count == 0)
        {
            sb.AppendLine("      <!-- Operation does not declare input parameters -->");
        }
        else
        {
            foreach (var parameter in parameters)
            {
                foreach (var line in FormatSnippetForEnvelope(parameter.SampleXml, operationName))
                {
                    sb.AppendLine("      " + line);
                }
            }
        }

        sb.AppendLine($"    </tns:{operationName}>");
        sb.AppendLine("  </soap:Body>");
        sb.AppendLine("</soap:Envelope>");
        return sb.ToString();
    }

    private static IEnumerable<string> FormatSnippetForEnvelope(string snippet, string operationName)
    {
        if (string.IsNullOrWhiteSpace(snippet))
        {
            yield break;
        }

        var trimmed = snippet.Trim();
        var root = ExtractRootLocalName(trimmed);

        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(root, operationName, StringComparison.OrdinalIgnoreCase))
        {
            var inner = ExtractInnerXml(trimmed);
            if (!string.IsNullOrWhiteSpace(inner))
            {
                foreach (var line in inner.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    yield return line.TrimEnd();
                }
                yield break;
            }
        }

        foreach (var line in trimmed.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return line.TrimEnd();
        }
    }

    private static string DecorateSample(string sampleXml, ValueMetadata metadata)
    {
        var replacement = metadata.Example
            ?? metadata.AllowedValues.FirstOrDefault()
            ?? (!string.IsNullOrWhiteSpace(metadata.Description) ? $"<!-- {metadata.Description} -->" : null);

        if (string.IsNullOrWhiteSpace(replacement))
        {
            return sampleXml;
        }

        var cleaned = Regex.Replace(sampleXml, @">(\\s*)\{value\}(\\s*)<", m => $">{replacement}<", RegexOptions.Multiline);
        if (cleaned.Contains("{value}"))
        {
            cleaned = cleaned.Replace("{value}", replacement);
        }
        return cleaned;
    }

    private static string? ExtractRootLocalName(string xml)
    {
        var start = xml.IndexOf('<');
        if (start < 0)
        {
            return null;
        }

        var end = xml.IndexOfAny(new[] { ' ', '>', '\r', '\n', '\t' }, start + 1);
        if (end < 0)
        {
            return null;
        }

        var tag = xml.Substring(start + 1, end - start - 1).Trim();
        if (tag.StartsWith("/"))
        {
            return null;
        }

        var colon = tag.IndexOf(':');
        return colon >= 0 ? tag[(colon + 1)..] : tag;
    }

    private static string? ExtractInnerXml(string xml)
    {
        var openEnd = xml.IndexOf('>');
        var closeStart = xml.LastIndexOf("</", StringComparison.OrdinalIgnoreCase);
        if (openEnd < 0 || closeStart <= openEnd)
        {
            return null;
        }

        return xml.Substring(openEnd + 1, closeStart - openEnd - 1).Trim();
    }

    private static string BuildElementSample(XElement element, SchemaContext schemaContext, int depth)
    {
        var name = element.Attribute("name")?.Value ?? "Element";
        var tag = $"tns:{name}";
        var sb = new StringBuilder();
        var indent = new string(' ', depth * 2);

        sb.AppendLine($"{indent}<{tag}>");
        sb.Append(BuildElementInner(element, schemaContext, depth + 1));
        sb.AppendLine($"{indent}</{tag}>");
        return sb.ToString();
    }

    private static string BuildComplexTypeSample(string elementName, XElement complexType, SchemaContext schemaContext, int depth)
    {
        var wrapper = new XElement(XsdNs + "element", new XAttribute("name", elementName), new XElement(XsdNs + "complexType", complexType.Elements()));
        return BuildElementSample(wrapper, schemaContext, depth);
    }

    private static string BuildElementInner(XElement element, SchemaContext schemaContext, int depth)
    {
        if (depth > 6)
        {
            return new string(' ', depth * 2) + "{...}\n";
        }

        var sb = new StringBuilder();
        var typeAttribute = element.Attribute("type")?.Value;
        var complexType = element.Element(XsdNs + "complexType");

        if (complexType != null)
        {
            sb.Append(BuildComplexTypeInner(complexType, schemaContext, depth));
        }
        else if (!string.IsNullOrWhiteSpace(typeAttribute))
        {
            var resolvedType = ResolveQualifiedName(element, typeAttribute);
            if (resolvedType != null && schemaContext.ComplexTypes.TryGetValue(resolvedType, out var referencedComplexType))
            {
                sb.Append(BuildComplexTypeInner(referencedComplexType, schemaContext, depth));
            }
            else
            {
                sb.AppendLine(new string(' ', depth * 2) + $"{{{resolvedType?.LocalName ?? "value"}}}");
            }
        }
        else
        {
            sb.AppendLine(new string(' ', depth * 2) + "{value}");
        }

        return sb.ToString();
    }

    private static string BuildComplexTypeInner(XElement complexType, SchemaContext schemaContext, int depth)
    {
        var sb = new StringBuilder();
        var handled = false;

        foreach (var container in complexType.Elements())
        {
            if (container.Name == XsdNs + "sequence" || container.Name == XsdNs + "all" || container.Name == XsdNs + "choice")
            {
                foreach (var child in container.Elements(XsdNs + "element"))
                {
                    var childName = child.Attribute("name")?.Value
                                    ?? ResolveQualifiedName(child, child.Attribute("ref")?.Value)?.LocalName
                                    ?? "Item";
                    var cloned = new XElement(child);
                    cloned.SetAttributeValue("name", childName);
                    sb.Append(BuildElementSample(cloned, schemaContext, depth));
                }
                handled = true;
            }
        }

        if (!handled)
        {
            sb.AppendLine(new string(' ', depth * 2) + "{value}");
        }

        return sb.ToString();
    }

    private static bool IsArray(XElement element)
    {
        var maxOccurs = element.Attribute("maxOccurs")?.Value;
        return string.Equals(maxOccurs, "unbounded", StringComparison.OrdinalIgnoreCase)
               || (int.TryParse(maxOccurs, out var value) && value > 1);
    }

    private static string? GetDocumentation(XElement element)
    {
        var annotation = element.Element(XsdNs + "annotation");
        return annotation?.Element(XsdNs + "documentation")?.Value?.Trim();
    }

    private static string ExtractLocalName(string? qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            return string.Empty;
        }

        var parts = qualifiedName.Split(':');
        return parts.Length == 2 ? parts[1] : parts[0];
    }

    private static XName? ResolveQualifiedName(XElement context, string? qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            return null;
        }

        var parts = qualifiedName.Split(':');
        if (parts.Length == 2)
        {
            var ns = context.GetNamespaceOfPrefix(parts[0]);
            return ns == null ? null : ns + parts[1];
        }

        var defaultNs = context.GetDefaultNamespace();
        return defaultNs == XNamespace.None ? null : defaultNs + qualifiedName;
    }

    private static string Indent(string text, int spaces)
    {
        var indent = new string(' ', spaces);
        var lines = text
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(line => string.IsNullOrWhiteSpace(line) ? indent.TrimEnd() : indent + line);
        return string.Join(Environment.NewLine, lines);
    }

    private sealed record PortOperation(string Name, string? InputMessage, string? OutputMessage, string? Documentation, WsdlDocument Document);

    private static string? BuildManifestValueHint(XElement parameter, string? typeName)
    {
        var hints = new List<string>();
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            hints.Add($"Type hint: {typeName}");
        }

        var expects = parameter.Attribute("expects")?.Value
                      ?? parameter.Element("Expects")?.Value;
        if (!string.IsNullOrWhiteSpace(expects))
        {
            hints.Add(expects);
        }

        return hints.Count == 0 ? null : string.Join("; ", hints);
    }

    private sealed class SchemaContext
    {
        public Dictionary<XName, XElement> Elements { get; } = new();
        public Dictionary<XName, XElement> ComplexTypes { get; } = new();
        public Dictionary<XName, XElement> SimpleTypes { get; } = new();
    }

    private sealed record ValueMetadata(string? Description, string? Example, IReadOnlyList<string> AllowedValues);

    private static ValueMetadata DescribeValueMetadata(XElement? element, SchemaContext context, XName? resolvedType)
    {
        if (element != null)
        {
            var inlineSimple = element.Element(XsdNs + "simpleType");
            if (inlineSimple != null)
            {
                return DescribeSimpleType(inlineSimple, context, new HashSet<XElement>());
            }

            var typeAttr = element.Attribute("type")?.Value;
            var resolved = resolvedType ?? ResolveQualifiedName(element, typeAttr);
            if (resolved != null && context.SimpleTypes.TryGetValue(resolved, out var namedSimple))
            {
                return DescribeSimpleType(namedSimple, context, new HashSet<XElement>());
            }

            return DescribeBuiltInType(resolved);
        }

        if (resolvedType != null && context.SimpleTypes.TryGetValue(resolvedType, out var externalSimple))
        {
            return DescribeSimpleType(externalSimple, context, new HashSet<XElement>());
        }

        return DescribeBuiltInType(resolvedType);
    }

    private static ValueMetadata DescribeSimpleType(XElement simpleType, SchemaContext context, HashSet<XElement> visited)
    {
        if (!visited.Add(simpleType))
        {
            return new ValueMetadata(null, null, Array.Empty<string>());
        }

        var restriction = simpleType.Element(XsdNs + "restriction");
        if (restriction != null)
        {
            var baseName = ResolveQualifiedName(restriction, restriction.Attribute("base")?.Value);
            var baseMetadata = baseName != null && context.SimpleTypes.TryGetValue(baseName, out var baseSimple)
                ? DescribeSimpleType(baseSimple, context, visited)
                : DescribeBuiltInType(baseName);

            var facets = new List<string>();
            var example = baseMetadata.Example;
            var allowedValues = new List<string>(baseMetadata.AllowedValues);

            var enums = restriction.Elements(XsdNs + "enumeration")
                .Select(e => e.Attribute("value")?.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim())
                .ToList();
            if (enums.Any())
            {
                allowedValues.AddRange(enums);
                facets.Add("Allowed values: " + string.Join(", ", enums.Take(5)) + (enums.Count > 5 ? ", ..." : string.Empty));
                example ??= enums.FirstOrDefault();
            }

            string? lengthDesc = BuildLengthDescription(restriction);
            if (!string.IsNullOrWhiteSpace(lengthDesc))
            {
                facets.Add(lengthDesc);
                example ??= GenerateLengthExample(lengthDesc);
            }

            var numericDesc = BuildNumericDescription(restriction);
            if (!string.IsNullOrWhiteSpace(numericDesc.Description))
            {
                facets.Add(numericDesc.Description);
            }
            example ??= numericDesc.Example ?? baseMetadata.Example;

            var pattern = restriction.Elements(XsdNs + "pattern")
                .Select(p => p.Attribute("value")?.Value)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                facets.Add("Pattern: " + pattern);
                example ??= TryBuildPatternExample(pattern, restriction);
            }

            visited.Remove(simpleType);
            var descriptionParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(baseMetadata.Description))
            {
                descriptionParts.Add(baseMetadata.Description);
            }
            descriptionParts.AddRange(facets);
            return new ValueMetadata(
                descriptionParts.Count == 0 ? null : string.Join("; ", descriptionParts),
                example,
                allowedValues);
        }

        var list = simpleType.Element(XsdNs + "list");
        if (list != null)
        {
            var itemType = ResolveQualifiedName(list, list.Attribute("itemType")?.Value);
            var itemMetadata = DescribeBuiltInType(itemType);
            var listExample = itemMetadata.Example != null ? $"{itemMetadata.Example} {itemMetadata.Example}" : null;
            visited.Remove(simpleType);
            return new ValueMetadata(
                $"Space-separated list of {itemMetadata.Description ?? itemType?.LocalName}",
                listExample,
                itemMetadata.AllowedValues);
        }

        visited.Remove(simpleType);
        return new ValueMetadata(null, null, Array.Empty<string>());
    }

    private static string? BuildLengthDescription(XElement restriction)
    {
        var length = restriction.Element(XsdNs + "length")?.Attribute("value")?.Value;
        if (!string.IsNullOrWhiteSpace(length))
        {
            return $"Exact length: {length}";
        }

        var minLength = restriction.Element(XsdNs + "minLength")?.Attribute("value")?.Value;
        var maxLength = restriction.Element(XsdNs + "maxLength")?.Attribute("value")?.Value;

        if (!string.IsNullOrWhiteSpace(minLength) && !string.IsNullOrWhiteSpace(maxLength))
        {
            return $"Length between {minLength} and {maxLength}";
        }

        if (!string.IsNullOrWhiteSpace(minLength))
        {
            return $"Minimum length: {minLength}";
        }

        if (!string.IsNullOrWhiteSpace(maxLength))
        {
            return $"Maximum length: {maxLength}";
        }

        return null;
    }

    private static (string? Description, string? Example) BuildNumericDescription(XElement restriction)
    {
        var minInclusive = restriction.Element(XsdNs + "minInclusive")?.Attribute("value")?.Value;
        var maxInclusive = restriction.Element(XsdNs + "maxInclusive")?.Attribute("value")?.Value;
        var minExclusive = restriction.Element(XsdNs + "minExclusive")?.Attribute("value")?.Value;
        var maxExclusive = restriction.Element(XsdNs + "maxExclusive")?.Attribute("value")?.Value;

        var parts = new List<string>();
        string? example = null;

        if (!string.IsNullOrWhiteSpace(minInclusive) && !string.IsNullOrWhiteSpace(maxInclusive))
        {
            parts.Add($"Range: {minInclusive} to {maxInclusive}");
            example = minInclusive;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(minInclusive))
            {
                parts.Add($"Minimum: {minInclusive}");
                example = minInclusive;
            }

            if (!string.IsNullOrWhiteSpace(maxInclusive))
            {
                parts.Add($"Maximum: {maxInclusive}");
                example ??= maxInclusive;
            }
        }

        if (!string.IsNullOrWhiteSpace(minExclusive))
        {
            parts.Add($"Greater than {minExclusive}");
            example ??= minExclusive;
        }

        if (!string.IsNullOrWhiteSpace(maxExclusive))
        {
            parts.Add($"Less than {maxExclusive}");
        }

        return (parts.Count == 0 ? null : string.Join("; ", parts), example);
    }

    private static string? TryBuildPatternExample(string pattern, XElement restriction)
    {
        var lengthFacet = restriction.Element(XsdNs + "length")?.Attribute("value")?.Value;
        var minLength = restriction.Element(XsdNs + "minLength")?.Attribute("value")?.Value;
        var digitsLength = ParseDigitsLength(pattern) ?? ParseDigitsLengthFromCharacterClass(pattern);
        var targetLength = digitsLength ?? TryParseInt(lengthFacet) ?? TryParseInt(minLength);
        if (targetLength.HasValue)
        {
            if (pattern.Contains("\\d") || pattern.Contains("[0-9]"))
            {
                return "1" + new string('0', Math.Max(0, targetLength.Value - 1));
            }
        }

        if (pattern.Contains("\\d") || pattern.Contains("[0-9]"))
        {
            return "12345";
        }

        return null;
    }

    private static int? ParseDigitsLength(string pattern)
    {
        var token = "\\d{";
        var index = pattern.IndexOf(token, StringComparison.Ordinal);
        if (index >= 0)
        {
            var end = pattern.IndexOf('}', index + token.Length);
            if (end > index)
            {
                if (int.TryParse(pattern.Substring(index + token.Length, end - (index + token.Length)), out var length))
                {
                    return length;
                }
            }
        }
        return null;
    }

    private static int? ParseDigitsLengthFromCharacterClass(string pattern)
    {
        var token = "[0-9]{";
        var index = pattern.IndexOf(token, StringComparison.Ordinal);
        if (index >= 0)
        {
            var end = pattern.IndexOf('}', index + token.Length);
            if (end > index)
            {
                if (int.TryParse(pattern.Substring(index + token.Length, end - (index + token.Length)), out var length))
                {
                    return length;
                }
            }
        }
        return null;
    }

    private static int? TryParseInt(string? text)
    {
        return int.TryParse(text, out var value) ? value : null;
    }

    private static (string? description, string? example) DescribeBuiltInType(XName? typeName)
    {
        if (typeName == null)
        {
            return (null, null);
        }

        if (typeName.Namespace == XsdNs)
        {
            switch (typeName.LocalName)
            {
                case "string":
                    return ("Text", "SampleText");
                case "normalizedString":
                    return ("Text (no line breaks)", "SampleText");
                case "token":
                    return ("Tokenized text", "TokenValue");
                case "boolean":
                    return ("Boolean (true/false)", "true");
                case "decimal":
                    return ("Decimal number", "123.45");
                case "integer":
                    return ("Integer", "123");
                case "int":
                    return ("32-bit integer", "123");
                case "long":
                    return ("64-bit integer", "123456789");
                case "short":
                    return ("16-bit integer", "1200");
                case "byte":
                    return ("8-bit signed integer", "64");
                case "positiveInteger":
                    return ("Positive integer", "1");
                case "nonNegativeInteger":
                    return ("Non-negative integer", "0");
                case "double":
                    return ("Double precision number", "123.45");
                case "float":
                    return ("Floating point number", "123.45");
                case "date":
                    return ("Date (YYYY-MM-DD)", DateTime.UtcNow.ToString("yyyy-MM-dd"));
                case "dateTime":
                    return ("Date & time (ISO 8601)", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                case "time":
                    return ("Time (HH:MM:SS)", DateTime.UtcNow.ToString("HH:mm:ss"));
                case "base64Binary":
                    return ("Base64 encoded binary", "U2FtcGxl");
                case "anyURI":
                    return ("URI", "https://api.example.com");
            }
        }

        return ($"Type: {typeName.LocalName}", null);
    }

    private sealed record WsdlDocument(Uri Source, XDocument Document);

    private sealed class DocumentLoader
    {
        private readonly HttpClient _client;

        public DocumentLoader(HttpClient client)
        {
            _client = client;
        }

        public async Task<List<WsdlDocument>> LoadAsync(WsdlParseRequest request, CancellationToken cancellationToken)
        {
            var documents = new List<WsdlDocument>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<Uri>();

            void Enqueue(string? uriString)
            {
                if (string.IsNullOrWhiteSpace(uriString))
                {
                    return;
                }

                if (Uri.TryCreate(uriString, UriKind.Absolute, out var absolute))
                {
                    if (visited.Add(absolute.AbsoluteUri))
                    {
                        queue.Enqueue(absolute);
                    }
                }
            }

            Enqueue(request.PrimarySource);
            foreach (var source in request.AdditionalSources)
            {
                Enqueue(source);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var document = await LoadSingleAsync(current, cancellationToken);
                documents.Add(document);

                if (!request.FollowImports)
                {
                    continue;
                }

                foreach (var import in document.Document.Descendants(WsdlNs + "import"))
                {
                    var location = import.Attribute("location")?.Value ?? import.Attribute("schemaLocation")?.Value;
                    Enqueue(ResolveUri(current, location)?.AbsoluteUri);
                }

                foreach (var schema in document.Document.Descendants(XsdNs + "schema"))
                {
                    foreach (var reference in schema.Elements().Where(e =>
                                 e.Name == XsdNs + "import" || e.Name == XsdNs + "include"))
                    {
                        var location = reference.Attribute("schemaLocation")?.Value;
                        Enqueue(ResolveUri(current, location)?.AbsoluteUri);
                    }
                }
            }

            return documents;
        }

        private async Task<WsdlDocument> LoadSingleAsync(Uri uri, CancellationToken cancellationToken)
        {
            var xml = await _client.GetStringAsync(uri, cancellationToken);
            return new WsdlDocument(uri, XDocument.Parse(xml, LoadOptions.PreserveWhitespace));
        }

        private static Uri? ResolveUri(Uri baseUri, string? relative)
        {
            if (string.IsNullOrWhiteSpace(relative))
            {
                return null;
            }

            if (Uri.TryCreate(relative, UriKind.Absolute, out var absolute))
            {
                return absolute;
            }

            return Uri.TryCreate(baseUri, relative, out var resolved) ? resolved : null;
        }
    }
}

