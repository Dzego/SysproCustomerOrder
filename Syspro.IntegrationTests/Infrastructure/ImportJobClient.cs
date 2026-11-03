using System.Net.Http.Json;
using Syspro.Api.DTOs;

namespace Syspro.IntegrationTests.Infrastructure;

public static class ImportJobClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // Polls the API as a real client would, and fails rather than waiting forever.
    public static async Task<ImportJobResponse> WaitForTerminalStatusAsync(
        HttpClient client,
        Uri location)
    {
        using var timeout = new CancellationTokenSource(Timeout);

        while (true)
        {
            var job = await client.GetFromJsonAsync<ImportJobResponse>(location, timeout.Token);

            Assert.NotNull(job);

            if (job.Status is "Completed" or "Failed")
            {
                return job;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }
}
