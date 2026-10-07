using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует discoverable OpenAPI-контракт всех потоковых export representations.</summary>
public sealed class ExportOpenApiContractTests
{
    [Fact]
    public async Task GeneratedVpnOpenApiHasYamlFormatAndAResolvableEndpointSchema()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(ProxyHarbor.Api.Controllers.VpnController).Assembly);
        builder.Services.AddOpenApi(options => options.AddOperationTransformer((operation, context, _) =>
        {
            ExportOpenApiContract.Apply(operation, context.Description.RelativePath);
            return Task.CompletedTask;
        }));
        await using var app = builder.Build();
        app.MapControllers();
        app.MapOpenApi();
        await app.StartAsync();
        using var client = new HttpClient();
        using var response = await client.GetAsync(new Uri(new Uri(app.Urls.Single()), "/openapi/v1.json"));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var operation = root.GetProperty("paths").GetProperty("/api/v1/vpn/export/{format}").GetProperty("get");
        var format = operation.GetProperty("parameters").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "format");
        Assert.Equal(["json", "txt", "clash"], format.GetProperty("schema").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        var success = operation.GetProperty("responses").GetProperty("200");
        Assert.Equal("binary", success.GetProperty("content").GetProperty("application/yaml").GetProperty("schema").GetProperty("format").GetString());
        var reference = success.GetProperty("content").GetProperty("application/json").GetProperty("schema")
            .GetProperty("properties").GetProperty("vpn").GetProperty("items").GetProperty("$ref").GetString();
        Assert.Equal("#/components/schemas/VpnEndpointResponse", reference);
        Assert.True(root.GetProperty("components").GetProperty("schemas").TryGetProperty("VpnEndpointResponse", out _));
        var countries = root.GetProperty("paths").GetProperty("/api/v1/vpn/countries").GetProperty("get");
        var countryFormat = countries.GetProperty("parameters").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "format");
        Assert.Equal(["uri", "clash", "all"], countryFormat.GetProperty("schema").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("uri", countryFormat.GetProperty("schema").GetProperty("default").GetString());
        Assert.Contains(countries.GetProperty("parameters").EnumerateArray(), item => item.GetProperty("name").GetString() == "protocol");
    }

    [Theory]
    [InlineData("api/v1/export/{format}", "X-Export-Offset", "X-Next-Offset")]
    [InlineData("api/v1/export/{format}/seek", "X-Export-Cursor", "X-Next-Cursor")]
    public void ExportMetadataDescribesFormatsSchemasContinuationAndFailures(
        string relativePath,
        string currentPageHeader,
        string nextPageHeader)
    {
        var operation = Operation();

        ExportOpenApiContract.Apply(operation, relativePath);

        var format = Assert.IsType<OpenApiParameter>(
            Assert.Single(operation.Parameters!, parameter => parameter.Name == "format"));
        var formatSchema = Assert.IsType<OpenApiSchema>(format.Schema);
        Assert.Equal(["json", "xml", "txt", "csv"],
            formatSchema.Enum!.Select(value => value!.GetValue<string>()));

        var success = Assert.IsType<OpenApiResponse>(operation.Responses!["200"]);
        Assert.Equal(["application/json", "application/xml", "text/csv", "text/plain"],
            success.Content!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(JsonSchemaType.Array, success.Content["application/json"].Schema!.Type);
        var xmlSchema = Assert.IsType<OpenApiSchema>(success.Content["application/xml"].Schema);
        Assert.Equal(JsonSchemaType.Array, xmlSchema.Type);
        Assert.Equal("proxies", xmlSchema.Xml!.Name);
        Assert.True(xmlSchema.Xml.Wrapped);
        Assert.Equal(JsonSchemaType.String, success.Content["text/plain"].Schema!.Type);
        Assert.Equal(JsonSchemaType.String, success.Content["text/csv"].Schema!.Type);
        Assert.Contains("Content-Disposition", success.Headers!.Keys);
        Assert.Contains("X-Export-Truncated", success.Headers.Keys);
        Assert.Contains(currentPageHeader, success.Headers.Keys);
        Assert.Contains(nextPageHeader, success.Headers.Keys);

        foreach (var status in new[] { "400", "429", "503" })
        {
            var problem = Assert.IsType<OpenApiResponse>(operation.Responses[status]);
            Assert.Equal(["application/json", "application/problem+json"],
                problem.Content!.Keys.Order(StringComparer.Ordinal));
        }
        Assert.Contains("Retry-After", ((OpenApiResponse)operation.Responses["429"]).Headers!.Keys);
        Assert.Contains("Retry-After", ((OpenApiResponse)operation.Responses["503"]).Headers!.Keys);
    }

    [Fact]
    public void NonExportOperationIsNotModified()
    {
        var operation = Operation();
        var originalContent = ((OpenApiResponse)operation.Responses!["200"]).Content;

        ExportOpenApiContract.Apply(operation, "api/v1/proxies");

        Assert.Same(originalContent, ((OpenApiResponse)operation.Responses["200"]).Content);
    }

    [Fact]
    public void VpnExportDocumentsYamlProfileQuotasAndFileAndProblemRepresentations()
    {
        var operation = Operation();
        ExportOpenApiContract.Apply(operation, "api/v1/vpn/export/{format}");
        var format = (OpenApiParameter)operation.Parameters![0];
        Assert.Equal(["json", "txt", "clash"], ((OpenApiSchema)format.Schema!).Enum!.Select(item => item!.GetValue<string>()));
        var success = (OpenApiResponse)operation.Responses!["200"];
        Assert.Equal(["application/json", "application/yaml", "text/plain"], success.Content!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(JsonSchemaType.Object, success.Content["application/json"].Schema!.Type);
        Assert.Equal("binary", success.Content["application/yaml"].Schema!.Format);
        Assert.Contains("X-Export-Profiles", success.Headers!.Keys);
        Assert.Contains("X-Export-Configurations", success.Headers.Keys);
        foreach (var code in new[] { "400", "404", "409", "429", "503" })
            Assert.Contains("application/problem+json", ((OpenApiResponse)operation.Responses[code]).Content!.Keys);
        Assert.Contains("Retry-After", ((OpenApiResponse)operation.Responses["503"]).Headers!.Keys);
    }

    private static OpenApiOperation Operation()
    {
        var proxyArray = new OpenApiSchema
        {
            Type = JsonSchemaType.Array,
            Items = new OpenApiSchema { Type = JsonSchemaType.Object }
        };
        var problem = new OpenApiSchema { Type = JsonSchemaType.Object };
        var operation = new OpenApiOperation
        {
            Parameters = [new OpenApiParameter { Name = "format" }],
            Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse
                {
                    Description = "OK",
                    Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
                    {
                        ["application/json"] = new() { Schema = proxyArray },
                        ["application/xml"] = new() { Schema = proxyArray },
                        ["text/plain"] = new() { Schema = proxyArray },
                        ["text/csv"] = new() { Schema = proxyArray }
                    }
                }
            }
        };
        foreach (var status in new[] { "400", "429", "503" })
            operation.Responses[status] = new OpenApiResponse
            {
                Description = status,
                Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
                {
                    ["application/json"] = new() { Schema = problem }
                }
            };
        return operation;
    }
}
