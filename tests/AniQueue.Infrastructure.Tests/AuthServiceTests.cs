using AniQueue.Core.Domain;
using AniQueue.Infrastructure.Security;

namespace AniQueue.Infrastructure.Tests;

public class AuthServiceTests
{
    [Fact]
    public async Task An_installation_with_no_password_is_not_locked()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await SeedProfileAsync(database);

        var auth = new AuthService(database.ContextFactory);

        Assert.False(await auth.IsLockedAsync());
    }

    [Fact]
    public async Task Setting_a_password_is_what_locks_it()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await SeedProfileAsync(database);

        var auth = new AuthService(database.ContextFactory);
        await auth.SetPasswordAsync("a good long password");

        Assert.True(await auth.IsLockedAsync());
    }

    [Fact]
    public async Task Clearing_the_password_opens_it_again()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await SeedProfileAsync(database);

        var auth = new AuthService(database.ContextFactory);
        await auth.SetPasswordAsync("a good long password");
        await auth.ClearPasswordAsync();

        Assert.False(await auth.IsLockedAsync());
        Assert.Null(await auth.SignInAsync("a good long password"));
    }

    [Fact]
    public async Task The_right_password_hands_back_a_stamp_and_the_wrong_one_hands_back_nothing()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await SeedProfileAsync(database);

        var auth = new AuthService(database.ContextFactory);
        await auth.SetPasswordAsync("a good long password");

        Assert.NotNull(await auth.SignInAsync("a good long password"));
        Assert.Null(await auth.SignInAsync("a good long passwore"));
    }

    [Fact]
    public async Task Changing_the_password_retires_the_stamp_every_other_device_is_holding()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await SeedProfileAsync(database);

        var auth = new AuthService(database.ContextFactory);
        await auth.SetPasswordAsync("the first one");

        var held = await auth.SignInAsync("the first one");
        Assert.NotNull(held);
        Assert.True(await auth.IsStampCurrentAsync(held));

        await auth.SetPasswordAsync("the second one");

        Assert.False(await auth.IsStampCurrentAsync(held));
    }

    [Fact]
    public async Task Clearing_the_password_retires_it_too()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await SeedProfileAsync(database);

        var auth = new AuthService(database.ContextFactory);
        await auth.SetPasswordAsync("the first one");

        var held = await auth.SignInAsync("the first one");
        await auth.ClearPasswordAsync();

        Assert.False(await auth.IsStampCurrentAsync(held));
    }

    [Fact]
    public async Task A_cookie_carrying_no_stamp_is_never_current()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await SeedProfileAsync(database);

        var auth = new AuthService(database.ContextFactory);
        await auth.SetPasswordAsync("a good long password");

        Assert.False(await auth.IsStampCurrentAsync(null));
        Assert.False(await auth.IsStampCurrentAsync(string.Empty));
        Assert.False(await auth.IsStampCurrentAsync("a stamp from somewhere else"));
    }

    [Fact]
    public async Task The_password_is_not_stored_as_the_password()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await SeedProfileAsync(database);

        var auth = new AuthService(database.ContextFactory);
        await auth.SetPasswordAsync("a good long password");

        await using var context = database.CreateContext();
        var stored = context.Profiles.Single(p => p.Id == Profile.DefaultProfileId).PasswordHash;

        Assert.NotNull(stored);
        Assert.DoesNotContain("a good long password", stored, StringComparison.Ordinal);
    }

    /// <summary>
    /// The row the initializer creates, with the stamp it fills in, because that is
    /// the state every request runs against.
    /// </summary>
    private static async Task SeedProfileAsync(SqliteTestDatabase database)
    {
        await using var context = database.CreateContext();

        context.Profiles.Add(new Profile
        {
            Id = Profile.DefaultProfileId,
            Name = "Default",
            CreatedAt = DateTimeOffset.UtcNow,
            LibraryKey = Profile.NewLibraryKey(),
            SecurityStamp = Profile.NewSecurityStamp()
        });

        await context.SaveChangesAsync();
    }
}
