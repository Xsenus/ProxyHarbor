using System.Net;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Tests;

public sealed class PaidSourceHttpFailureTests
{
    [Fact]
    public void ForbiddenDoesNotClaimExpirationOrInvalidKey()
    {
        var result = PaidSourceHttpFailure.From(HttpStatusCode.Forbidden);
        Assert.Equal("error", result.Status);
        Assert.Contains("403", result.Message, StringComparison.Ordinal);
        Assert.Contains("срок действия не подтверждён", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "invalid")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited")]
    [InlineData(HttpStatusCode.InternalServerError, "error")]
    [InlineData(HttpStatusCode.NotFound, "error")]
    [InlineData(null, "error")]
    public void DistinguishesAuthenticationRateLimitAndAvailability(HttpStatusCode? code, string expected) =>
        Assert.Equal(expected, PaidSourceHttpFailure.From(code).Status);
}
