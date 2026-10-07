using CclCrmProxyCore.Helpers;
using Xunit;

namespace CclWebApiCore.Tests;

public class CRMHelperTests
{
    // Regression for the "swallow to null" fix (session incident 2026-07-10): a missing/empty
    // CRM connection string must return null — logged, not throwing — so callers that guard with
    // `if (svc != null)` keep working and the failure reason is surfaced instead of resurfacing
    // two layers away as an opaque ArgumentNullException in CclServiceContext.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetCrmService_NullOrWhitespaceConnString_ReturnsNull(string? connString)
    {
        var service = CRMHelper.GetCrmService(connString!);
        Assert.Null(service);
    }
}
