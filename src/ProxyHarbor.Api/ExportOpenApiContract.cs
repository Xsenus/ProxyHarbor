using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace ProxyHarbor.Api;

/// <summary>Дополняет OpenAPI точным полиморфным контрактом потоковых export endpoint.</summary>
internal static class ExportOpenApiContract
{
    private static readonly string[] Formats = ["json", "xml", "txt", "csv"];

    internal static void Apply(OpenApiOperation operation, string? relativePath)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (relativePath?.StartsWith("api/v1/export/{format}", StringComparison.OrdinalIgnoreCase) != true)
            return;

        // Один MVC response type создаёт ProxyDto[] schema; здесь разделяем structured
        // JSON/XML и текстовые TXT/CSV representations, которые атрибуты одного status
        // самостоятельно выразить не могут.
        var responses = operation.Responses ??
            throw new InvalidOperationException("OpenAPI export metadata не содержит responses.");
        if (!responses.TryGetValue("200", out var successElement) ||
            successElement is not OpenApiResponse success ||
            success.Content is null ||
            !success.Content.TryGetValue("application/json", out var structured) ||
            structured.Schema is null)
            throw new InvalidOperationException("OpenAPI export 200 metadata не содержит ProxyDto[] schema.");

        var structuredSchema = structured.Schema;
        if (structuredSchema.Items is null)
            throw new InvalidOperationException("OpenAPI export 200 metadata не содержит ProxyDto[] items schema.");
        success.Content.Clear();
        success.Content["application/json"] = new OpenApiMediaType { Schema = structuredSchema };
        success.Content["application/xml"] = new OpenApiMediaType
        {
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                Items = structuredSchema.Items,
                Description = "Корневой <proxies> содержит последовательность <proxy> элементов.",
                Xml = new OpenApiXml { Name = "proxies", Wrapped = true }
            }
        };
        success.Content["text/plain"] = TextRepresentation("Одна canonical proxy URL на строку.");
        success.Content["text/csv"] = TextRepresentation("UTF-8 CSV с заголовком и полным ProxyDto contract.");

        success.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
        success.Headers["Content-Disposition"] = Header(JsonSchemaType.String,
            "Attachment filename выбранного export-формата.");
        success.Headers["X-Export-Limit"] = Header(JsonSchemaType.Integer,
            "Максимальное число строк текущей страницы.");
        success.Headers["X-Export-Truncated"] = Header(JsonSchemaType.Boolean,
            "True, если доступна следующая страница.");

        var seekMode = relativePath.EndsWith("/seek", StringComparison.OrdinalIgnoreCase);
        if (seekMode)
        {
            success.Headers["X-Export-Cursor"] = Header(JsonSchemaType.String,
                "Cursor, с которого сформирована текущая страница.");
            success.Headers["X-Next-Cursor"] = Header(JsonSchemaType.String,
                "Непрозрачный cursor следующей страницы; отсутствует на последней.");
        }
        else
        {
            success.Headers["X-Export-Offset"] = Header(JsonSchemaType.Integer,
                "Offset текущей legacy-страницы.");
            success.Headers["X-Next-Offset"] = Header(JsonSchemaType.Integer,
                "Offset следующей legacy-страницы; отсутствует на последней.");
        }

        var formatParameter = (operation.Parameters ??
                throw new InvalidOperationException("OpenAPI export metadata не содержит parameters."))
            .OfType<OpenApiParameter>()
            .Single(parameter => string.Equals(parameter.Name, "format", StringComparison.Ordinal));
        formatParameter.Description = "Формат ответа: json, xml, txt или csv.";
        formatParameter.Schema = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Enum = Formats.Select(format => (JsonNode)JsonValue.Create(format)!).ToList()
        };

        foreach (var status in new[] { "400", "429", "503" })
            NormalizeProblemResponse(operation, status);

        AddRetryAfter(operation, "429");
        AddRetryAfter(operation, "503");
    }

    private static OpenApiMediaType TextRepresentation(string description) => new()
    {
        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Description = description }
    };

    private static OpenApiHeader Header(JsonSchemaType type, string description) => new()
    {
        Description = description,
        Schema = new OpenApiSchema { Type = type }
    };

    private static void NormalizeProblemResponse(OpenApiOperation operation, string status)
    {
        if (operation.Responses is null ||
            !operation.Responses.TryGetValue(status, out var responseElement) ||
            responseElement is not OpenApiResponse response || response.Content is null)
            throw new InvalidOperationException($"OpenAPI export metadata не содержит response {status}.");
        var schema = response.Content.Values.Select(media => media.Schema).FirstOrDefault(value => value is not null)
            ?? throw new InvalidOperationException($"OpenAPI export response {status} не содержит ProblemDetails schema.");
        response.Content.Clear();
        response.Content["application/problem+json"] = new OpenApiMediaType { Schema = schema };
        // RateLimiter пишет ProblemDetails напрямую через WriteAsJsonAsync.
        response.Content["application/json"] = new OpenApiMediaType { Schema = schema };
    }

    private static void AddRetryAfter(OpenApiOperation operation, string status)
    {
        if (operation.Responses is null ||
            !operation.Responses.TryGetValue(status, out var responseElement) ||
            responseElement is not OpenApiResponse response)
            throw new InvalidOperationException($"OpenAPI export metadata не содержит response {status}.");
        response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
        response.Headers["Retry-After"] = Header(JsonSchemaType.Integer,
            "Минимальная задержка перед повторным запросом, секунды.");
    }
}
