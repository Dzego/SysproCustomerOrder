using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Syspro.Api.Data;

namespace Syspro.IntegrationTests.Infrastructure;

// Simulates another import committing the same legacy ID between this import's
// lookup and its save, which produces a real unique index failure at save time.
public sealed class ConcurrentImportInterceptor(
    string connectionString,
    string legacyCustomerId) : SaveChangesInterceptor
{
    private bool _hasInserted;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (!_hasInserted)
        {
            _hasInserted = true;

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(connectionString)
                .Options;

            await using var other = new AppDbContext(options);

            other.Customers.Add(TestData.Customer(legacyCustomerId, "Concurrent Import"));
            await other.SaveChangesAsync(cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
