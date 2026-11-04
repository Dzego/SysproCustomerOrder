using Microsoft.EntityFrameworkCore;
using Syspro.Api.Data;
using Syspro.Api.Repositories;
using Syspro.Tests.TestSupport;
using Xunit;

namespace Syspro.Tests.Repositories;

public class RepositoryCancellationTests
{
    private readonly SaveChangesTokenCaptureInterceptor _interceptor = new();

    private AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(_interceptor)
            .Options;

        return new AppDbContext(options);
    }

    [Fact]
    public async Task GetByLegacyCustomerIdAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        await using var dbContext = CreateDbContext();
        var repository = new CustomerRepository(dbContext);

        // Act
        var action = () => repository.GetByLegacyCustomerIdAsync(
            "0000012345",
            new CancellationToken(canceled: true));

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
    }

    [Fact]
    public async Task ImportRepository_SaveChangesAsync_PassesTokenToDbContext()
    {
        // Arrange
        await using var dbContext = CreateDbContext();
        var repository = new ImportRepository(dbContext);

        using var cancellationSource = new CancellationTokenSource();

        // Act
        await repository.SaveChangesAsync(cancellationSource.Token);

        // Assert
        Assert.Equal(cancellationSource.Token, _interceptor.CapturedToken);
    }
}
