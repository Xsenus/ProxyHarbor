using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace ProxyHarbor.Api;

/// <summary>Дополняет OpenAPI точным полиморфным контрактом потоковых export endpoint.</summary>
internal static class ExportOpenApiContract
{
    private static readonly string[] Formats = ["json", "xml", "txt", "csv"];
    private static readonly string[] VpnFormats = ["json", "txt", "clash"];
    private static readonly string[] VpnCountryFormats = ["uri", "clash", "all"];

    internal static void Apply(OpenApiOperation operation, string? relativePath)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (string.Equals(relativePath, "api/v1/vpn/countries", StringComparison.OrdinalIgnoreCase))
        {
            var format = operation.Parameters?.OfType<OpenApiParameter>().SingleOrDefault(parameter => parameter.Name == "format");
            if (format?.Schema is OpenApiSchema schema)
            {
                schema.Enum = VpnCountryFormats.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
                format.Description = "uri: ready links (default); clash: saved YAML checked after its latest observation; all: unique endpoints eligible for either representation. Counts precede dependency validation and access quotas.";
            }
            return;
        }
        if (string.Equals(relativePath, "api/v1/vpn/export/{format}", StringComparison.OrdinalIgnoreCase))
        {
            ApplyVpn(operation);
            return;
        }
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

    private static void ApplyVpn(OpenApiOperation operation)
    {
        var format = operation.Parameters?.OfType<OpenApiParameter>().Single(parameter => parameter.Name == "format")
            ?? throw new InvalidOperationException("OpenAPI VPN export metadata не содержит format.");
        format.Description = "json/txt: опубликованные URI; clash: полные сохранённые YAML-конфигурации и статические зависимости.";
        format.Schema = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Enum = VpnFormats.Select(value => (JsonNode)JsonValue.Create(value)!).ToList()
        };
        operation.Responses ??= new OpenApiResponses();
        operation.Responses["200"] = new OpenApiResponse
        {
            Description = "Готовый файл с ограничениями free/paid. Clash считает каждый зависимый профиль в лимите.",
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                ["application/json"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                        {
                            ["access"] = new OpenApiSchema { Type = JsonSchemaType.Object },
                            ["vpn"] = new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchemaReference("VpnEndpointResponse") }
                        }
                    }
                },
                ["text/plain"] = TextRepresentation("Опубликованный connectionUri на каждой строке; комментарий описывает free-доступ."),
                ["application/yaml"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary", Description = "Полный Clash YAML с группой выбора, правилом MATCH и статическими зависимостями." } }
            },
            Headers = new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal)
            {
                ["Content-Disposition"] = Header(JsonSchemaType.String, "Attachment filename выбранного export-формата."),
                ["X-Access-Tier"] = Header(JsonSchemaType.String, "free либо paid."),
                ["X-Catalog-Total"] = Header(JsonSchemaType.Integer, "Число подходящих основных строк; для clash до проверки зависимостей."),
                ["X-Export-Limit"] = Header(JsonSchemaType.Integer, "Лимит URI либо всех выдаваемых Clash-профилей."),
                ["X-Export-Profiles"] = Header(JsonSchemaType.Integer, "Clash: фактическое число выдаваемых профилей, включая зависимости."),
                ["X-Export-Configurations"] = Header(JsonSchemaType.Integer, "Clash: число основных конфигураций выбора."),
                ["Link"] = Header(JsonSchemaType.String, "Для free: ссылка upgrade на /account.")
            }
        };
        foreach (var status in new[] { "400", "404", "409", "429", "503" })
        {
            operation.Responses[status] = new OpenApiResponse
            {
                Description = status switch { "404" => "Нет свежей полной Clash-цепочки в пределах лимита.", "409" => "Некорректный YAML либо превышение ограничений полного файла.", "503" => "Clash-экспорт занят либо истёк лимит времени.", _ => "ProblemDetails" },
                Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
                {
                    ["application/problem+json"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.Object } },
                    ["application/json"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.Object } }
                }
            };
        }
        AddRetryAfter(operation, "429");
        AddRetryAfter(operation, "503");
    }

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
