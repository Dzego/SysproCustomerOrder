using DotNet.Testcontainers.Configurations;

namespace Syspro.IntegrationTests.Infrastructure;

// Only a missing Docker daemon skips a test, and the reason appears in the test
// results. Once Docker is reachable, any failure, including a container that
// will not start, fails the tests.
//
// Set SYSPRO_REQUIRE_DOCKER=true where these tests must run, such as in CI, so
// that a missing daemon fails the run instead of skipping the tests.
internal static class DockerAvailability
{
    private const string RequireDockerVariable = "SYSPRO_REQUIRE_DOCKER";

    public const string SkipReason =
        "Requires Docker, but no Docker daemon is reachable. Start Docker to run this test.";

    private static readonly Lazy<bool> IsAvailable = new(() =>
        TestcontainersSettings.OS.DockerEndpointAuthConfig is not null);

    public static bool ShouldSkip =>
        !IsAvailable.Value &&
        !string.Equals(
            Environment.GetEnvironmentVariable(RequireDockerVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (DockerAvailability.ShouldSkip)
        {
            Skip = DockerAvailability.SkipReason;
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (DockerAvailability.ShouldSkip)
        {
            Skip = DockerAvailability.SkipReason;
        }
    }
}
