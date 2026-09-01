using AniQueue.Core.Domain;
using AniQueue.Core.Security;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AniQueue.Infrastructure.Security;

/// <summary>
/// Reads and writes the two columns the lock is made of, both on the profile row.
/// </summary>
/// <remarks>
/// <b>It holds the pair in memory, which is what makes the lock affordable.</b>
/// Every request asks twice — once whether anything is locked, once whether the
/// cookie's stamp is still current — and a backlog page is one request plus fifty
/// posters. Reading the row each time would put fifty queries behind one screen to
/// answer a question that changes when somebody presses a button.
///
/// A singleton for the same reason, and safe as one because it owns every write:
/// nothing else in the application touches these columns, so the copy here cannot
/// fall behind the row. Two threads filling it at once agree on the answer.
/// </remarks>
public sealed class AuthService(IDbContextFactory<AniQueueDbContext> contextFactory) : IAuthService
{
    /// <summary>The stored pair, or null until it has been read.</summary>
    private volatile Credential? _cached;

    public async Task<bool> IsLockedAsync(CancellationToken cancellationToken = default)
        => (await ReadAsync(cancellationToken)).Hash is { Length: > 0 };

    public async Task<string?> SignInAsync(string password, CancellationToken cancellationToken = default)
    {
        var credential = await ReadAsync(cancellationToken);

        return PasswordHash.Verify(credential.Hash, password) ? credential.Stamp : null;
    }

    public async Task<string> SetPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        return (await WriteAsync(PasswordHash.Create(password), cancellationToken)).Stamp
            ?? throw new InvalidOperationException("A password was set without a stamp to go with it.");
    }

    public async Task ClearPasswordAsync(CancellationToken cancellationToken = default)
        => await WriteAsync(null, cancellationToken);

    public async Task<bool> IsStampCurrentAsync(string? stamp, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(stamp))
        {
            return false;
        }

        return (await ReadAsync(cancellationToken)).Stamp == stamp;
    }

    /// <summary>The stored pair, from memory when it is there and the row when it is not.</summary>
    private async Task<Credential> ReadAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var stored = await context.Profiles
            .AsNoTracking()
            .Where(p => p.Id == Profile.DefaultProfileId)
            .Select(p => new { p.PasswordHash, p.SecurityStamp })
            .FirstOrDefaultAsync(cancellationToken);

        // No profile row at all is a database the initializer has not reached, which
        // is not a state a request can be served in. Treated as unlocked rather than
        // cached, so the answer is not held for the life of the process.
        if (stored is null)
        {
            return new Credential(null, null);
        }

        var credential = new Credential(stored.PasswordHash, stored.SecurityStamp);
        _cached = credential;
        return credential;
    }

    /// <summary>
    /// Stores a hash, or removes one, and mints a stamp either way — which is what
    /// signs the other devices out.
    /// </summary>
    private async Task<Credential> WriteAsync(string? hash, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var profile = await context.Profiles
            .FirstOrDefaultAsync(p => p.Id == Profile.DefaultProfileId, cancellationToken);

        if (profile is null)
        {
            throw new InvalidOperationException("There is no profile to hold a password.");
        }

        profile.PasswordHash = hash;
        profile.SecurityStamp = Profile.NewSecurityStamp();

        await context.SaveChangesAsync(cancellationToken);

        var written = new Credential(profile.PasswordHash, profile.SecurityStamp);
        _cached = written;

        return written;
    }

    /// <summary>The password and the stamp, which are always read and written together.</summary>
    private sealed record Credential(string? Hash, string? Stamp);
}
