using Microsoft.OpenApi;
using ProxyHarbor.Api;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует discoverable OpenAPI-контракт всех потоковых export representations.</summary>
public sealed class ExportOpenApiContractTests
{
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
