using Fynydd.Umbraco.Search.Postgres.Services;

namespace Umbraco.Search.Postgres.Tests;

public sealed class TextReplacementProviderTests
{
    [Fact]
    public void EmptyTextReplacementProvider_ReturnsEmptyMap()
    {
        var provider = new EmptyTextReplacementProvider();

        Assert.Empty(provider.GetReplacements());
    }
}
