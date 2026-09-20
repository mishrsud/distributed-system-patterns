using Npgsql;
using SampleApplication;
using Xunit;

namespace Integration.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Round_trips_a_dependency_record()
    {
        var expected = new DependencyRecord(Guid.NewGuid(), "postgresql");

        await using var connection = new NpgsqlConnection(fixture.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE records (id uuid NOT NULL, value text NOT NULL);
            INSERT INTO records (id, value) VALUES (@id, @value);
            SELECT id, value FROM records WHERE id = @id;
            """;
        command.Parameters.AddWithValue("id", expected.Id);
        command.Parameters.AddWithValue("value", expected.Value);

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected, new DependencyRecord(reader.GetGuid(0), reader.GetString(1)));
    }
}
