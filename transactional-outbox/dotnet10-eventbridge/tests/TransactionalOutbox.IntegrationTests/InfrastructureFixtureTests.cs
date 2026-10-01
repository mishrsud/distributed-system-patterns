namespace TransactionalOutbox.IntegrationTests;

public sealed class InfrastructureFixtureTests
{
    [Fact]
    public void RejectsRemoteSameNamedDatabaseWithoutOpeningAConnection()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new InfrastructureFixture(
                "Server=sql.example.test,1433;Database=TransactionalOutboxTests;" +
                "User Id=sa;Password=Local_dev_Only_123!;TrustServerCertificate=True",
                destructiveDatabaseTestsOptIn: null));

        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptsLocalOverrideWithoutOpeningAConnection()
    {
        var fixture = new InfrastructureFixture(
            "Server=127.0.0.1,14339;Database=TransactionalOutboxTests;" +
            "User Id=sa;Password=Local_dev_Only_123!;TrustServerCertificate=True",
            destructiveDatabaseTestsOptIn: null);

        Assert.Contains("127.0.0.1", fixture.ConnectionString, StringComparison.Ordinal);
    }
}
