using System.ComponentModel.DataAnnotations;
using System.Reflection;
using ProxyHarbor.Api.Controllers;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Tests;

/// <summary>Фиксирует fail-closed model validation публичных query-параметров.</summary>
public sealed class PublicApiContractTests
{
    [Theory]
    [InlineData(nameof(ProxiesController.Get))]
    [InlineData(nameof(ProxiesController.Seek))]
    [InlineData(nameof(ProxiesController.Export))]
    [InlineData(nameof(ProxiesController.ExportSeek))]
    public void ProtocolFilterRejectsUndefinedNumericEnum(string actionName)
    {
        var action = typeof(ProxiesController).GetMethod(actionName)
            ?? throw new InvalidOperationException($"Action {actionName} не найден.");
        var parameter = action.GetParameters().Single(item => item.Name == "protocol");
        var validation = parameter.GetCustomAttribute<EnumDataTypeAttribute>();

        Assert.NotNull(validation);
        Assert.True(validation.IsValid(null));
        Assert.True(validation.IsValid(ProxyProtocol.Socks5));
        Assert.False(validation.IsValid((ProxyProtocol)999));
    }
}
