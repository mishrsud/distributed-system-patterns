using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Integration.Tests.PostgreSql;

[CollectionDefinition(Name)]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "This type defines an xUnit test collection.")]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSql";
}
