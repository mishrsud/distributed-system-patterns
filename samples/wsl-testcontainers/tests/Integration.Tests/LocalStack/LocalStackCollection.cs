using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Integration.Tests.LocalStack;

[CollectionDefinition(Name, DisableParallelization = true)]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "This type defines an xUnit test collection.")]
public sealed class LocalStackCollection : ICollectionFixture<LocalStackFixture>
{
    public const string Name = "LocalStack";
}
