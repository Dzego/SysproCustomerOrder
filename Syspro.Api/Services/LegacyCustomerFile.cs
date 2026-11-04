namespace Syspro.Api.Services;

public static class LegacyCustomerFile
{
    private const int BufferSize = 81920;

    // Enforces the limit while reading, not only from the initial length, so that a
    // file growing during the read cannot exceed it.
    public static async Task<byte[]> ReadAsync(
        IHostEnvironment environment,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            GetPath(environment),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            useAsync: true);

        if (file.Length > maxBytes)
        {
            throw new ImportInputTooLargeException(maxBytes);
        }

        using var content = new MemoryStream((int)file.Length);

        var buffer = new byte[BufferSize];
        int bytesRead;

        while ((bytesRead = await file.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (content.Length + bytesRead > maxBytes)
            {
                throw new ImportInputTooLargeException(maxBytes);
            }

            content.Write(buffer, 0, bytesRead);
        }

        return content.ToArray();
    }

    private static string GetPath(IHostEnvironment environment)
    {
        return Path.Combine(
            environment.ContentRootPath,
            "LegacyData",
            "customers_legacy.dat");
    }
}
