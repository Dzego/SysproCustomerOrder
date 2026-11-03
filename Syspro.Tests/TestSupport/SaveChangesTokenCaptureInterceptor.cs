using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Syspro.Tests.TestSupport;

// Records the token EF Core receives, which proves it reached SaveChangesAsync.
public sealed class SaveChangesTokenCaptureInterceptor : SaveChangesInterceptor
{
    public CancellationToken? CapturedToken { get; private set; }

    public Action? OnSaving { get; set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CapturedToken = cancellationToken;
        OnSaving?.Invoke();

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
