using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Syspro.IntegrationTests.Infrastructure;

public sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";

    public string ApplicationName { get; set; } = "Syspro.IntegrationTests";

    public string ContentRootPath { get; set; } = contentRootPath;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
