namespace Integration.Tests.Infrastructure;

internal static class UniqueName
{
    internal static string Create(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
