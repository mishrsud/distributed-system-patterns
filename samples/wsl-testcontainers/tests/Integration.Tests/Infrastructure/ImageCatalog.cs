namespace Integration.Tests.Infrastructure;

internal static class ImageCatalog
{
    internal const string MsSql = "mcr.microsoft.com/mssql/server:2025-CU8-GDR1-ubuntu-24.04";
    internal const string PostgreSql = "postgres:18.6-alpine3.23";
    internal const string LocalStack = "localstack/localstack:2026.8.2";
}
