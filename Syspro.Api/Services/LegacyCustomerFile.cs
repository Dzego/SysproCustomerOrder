namespace Syspro.Api.Services;

public static class LegacyCustomerFile
{
    public static string GetPath(IHostEnvironment environment)
    {
        return Path.Combine(
            environment.ContentRootPath,
            "LegacyData",
            "customers_legacy.dat");
    }
}
