using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Syspro.Api.Data;
using Syspro.Api.Models;

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
        // Waits for the save that writes customers, not the one that schedules a job.
        var isSavingCustomers = eventData.Context!.ChangeTracker
            .Entries<Customer>()
            .Any(entry => entry.State == EntityState.Added);

        if (isSavingCustomers && !_hasInserted)
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
