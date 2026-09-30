using Microsoft.Data.SqlClient;
using SampleApplication;
using Xunit;

namespace Integration.Tests.SqlServer;

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Round_trips_a_dependency_record()
    {
        var expected = new DependencyRecord(Guid.NewGuid(), "sql-server");

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Records (Id uniqueidentifier NOT NULL, Value nvarchar(100) NOT NULL);
            INSERT INTO Records (Id, Value) VALUES (@id, @value);
            SELECT Id, Value FROM Records WHERE Id = @id;
            """;
        command.Parameters.AddWithValue("@id", expected.Id);
        command.Parameters.AddWithValue("@value", expected.Value);

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected, new DependencyRecord(reader.GetGuid(0), reader.GetString(1)));
    }
}
