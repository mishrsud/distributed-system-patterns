using Xunit;

namespace Integration.Tests.Infrastructure;

public sealed class UniqueNameTests
{
    [Fact]
    public void Create_with_the_same_prefix_produces_distinct_lowercase_hyphenated_names()
    {
        var first = UniqueName.Create("sample");
        var second = UniqueName.Create("sample");

        Assert.StartsWith("sample-", first);
        Assert.Matches("^[a-z0-9-]+$", first);
        Assert.StartsWith("sample-", second);
        Assert.Matches("^[a-z0-9-]+$", second);
        Assert.NotEqual(first, second);
    }
}
