using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Integration.Tests.SqlServer;

[CollectionDefinition(Name)]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "This type defines an xUnit test collection.")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
