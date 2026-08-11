using InsertFacturasSQL.Services;

namespace InsertFacturasSQL.Tests;

public sealed class ProviderResolverTests
{
    [Fact]
    public void NormalizeCompanyName_IgnoresNonDistinctiveTermsAndPunctuation()
    {
        Assert.Equal(
            ProviderResolver.NormalizeCompanyName("OpenAI OpCo, LLC"),
            ProviderResolver.NormalizeCompanyName("OpenAI LLC"));
    }

    [Fact]
    public void NormalizeCompanyName_RemovesAccentsAndSpacingDifferences()
    {
        Assert.Equal("CARREFOUR", ProviderResolver.NormalizeCompanyName("  Carrefour, S.A.  "));
    }
}
