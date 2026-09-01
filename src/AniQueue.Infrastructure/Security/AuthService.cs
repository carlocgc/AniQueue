using AniQueue.Core.Domain;
using AniQueue.Core.Security;
using AniQueue.Core.Settings;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Security;

/// <summary>
/// Keeps the switch in <c>userconfig.json</c> and the password on the profile row
/// in step with each other.
/// </summary>
/// <remarks>
/// <b>It holds the stored pair in memory, which is what makes the lock affordable.</b>
/// Every request asks twice — once what the lock is doing, once whether the
/// cookie's stamp is still current — and a backlog page is one request plus fifty
/// posters. Reading the row each time would put fifty queries behind one screen to
/// answer a question that changes when somebody presses a button.
///
/// A singleton for the same reason, and safe as one because it owns every write:
/// nothing else in the application touches these columns, so the copy here cannot
/// fall behind the row. Two threads filling it at once agree on the answer.
/// </remarks>
public sealed class AuthService(
    IDbContextFactory<AniQueueDbContext> contextFactory,
    IUserSettingsStore settings,
    IOptionsMonitor<AuthOptions> options) : IAuthService
{
    /// <summary>The stored pair, or null until it has been read.</summary>
    private volatile Credential? _cached;

    public async Task<AuthState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (!options.CurrentValue.Enabled)
        {
            return AuthState.Open;
        }

        return (await ReadAsync(cancellationToken)).Hash is { Length: > 0 }
            ? AuthState.Locked
            : AuthState.NeedsPassword;
    }

    public async Task<string?> SignInAsync(string password, CancellationToken cancellationToken = default)
    {
        var credential = await ReadAsync(cancellationToken);

        return PasswordHash.Verify(credential.Hash, password) ? credential.Stamp : null;
    }

    public async Task<AuthChange> SetPasswordAsync(
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var written = await WriteAsync(PasswordHash.Create(password), cancellationToken);

        return new AuthChange(written.Stamp, await SwitchAsync(true, cancellationToken));
    }

    public async Task<AuthChange> RemovePasswordAsync(CancellationToken cancellationToken = default)
    {
        await WriteAsync(null, cancellationToken);

        return new AuthChange(null, await SwitchAsync(false, cancellationToken));
    }

    public async Task<bool> ForgetPasswordIfDisabledAsync(CancellationToken cancellationToken = default)
    {
        if (options.CurrentValue.Enabled)
        {
            return false;
        }

        if ((await ReadAsync(cancellationToken)).Hash is not { Length: > 0 })
        {
            return false;
        }

        // The switch is already off in the file, so only the password half is left to
        // undo. Nothing is written back to the file, which is what makes this the
        // stable state it looks like rather than a trigger that fires once.
        await WriteAsync(null, cancellationToken);

        return true;
    }

    public async Task<bool> IsStampCurrentAsync(
        string? stamp,
        CancellationToken cancellationToken = default)
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
        // is not a state a request can be served in. Answered rather than cached, so
        // the answer is not held for the life of the process.
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
            .FirstOrDefaultAsync(p => p.Id == Profile.DefaultProfileId, cancellationToken)
            ?? throw new InvalidOperationException("There is no profile to hold a password.");

        profile.PasswordHash = hash;
        profile.SecurityStamp = Profile.NewSecurityStamp();

        await context.SaveChangesAsync(cancellationToken);

        var written = new Credential(profile.PasswordHash, profile.SecurityStamp);
        _cached = written;

        return written;
    }

    /// <summary>
    /// Writes <c>Auth:Enabled</c>, and reports rather than throws when the file will
    /// not take it.
    /// </summary>
    private async Task<string?> SwitchAsync(bool enabled, CancellationToken cancellationToken)
    {
        var current = settings.Read();

        if (current.AuthEnabled == enabled)
        {
            return null;
        }

        var result = await settings.SaveAsync(
            current with { AuthEnabled = enabled },
            cancellationToken);

        return result.Saved ? null : result.Error ?? $"{result.Path} could not be written.";
    }

    /// <summary>The password and the stamp, which are always read and written together.</summary>
    private sealed record Credential(string? Hash, string? Stamp);
}
