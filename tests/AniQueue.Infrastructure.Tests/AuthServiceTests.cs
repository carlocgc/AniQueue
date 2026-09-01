using AniQueue.Core.Domain;
using AniQueue.Core.Security;
using AniQueue.Core.Settings;
using AniQueue.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

public class AuthServiceTests
{
    [Fact]
    public async Task A_fresh_installation_asks_for_nothing()
    {
        await using var world = await AuthWorld.CreateAsync();

        Assert.Equal(AuthState.Open, await world.Auth.GetStateAsync());
    }

    [Fact]
    public async Task Setting_a_password_locks_it_and_turns_the_switch_on()
    {
        await using var world = await AuthWorld.CreateAsync();

        var changed = await world.Auth.SetPasswordAsync("a good long password");

        Assert.Null(changed.SettingsError);
        Assert.Equal(AuthState.Locked, await world.Auth.GetStateAsync());
        Assert.True(world.Settings.Current.AuthEnabled);
    }

    [Fact]
    public async Task Removing_the_password_opens_it_and_turns_the_switch_off()
    {
        await using var world = await AuthWorld.CreateAsync();

        await world.Auth.SetPasswordAsync("a good long password");
        var changed = await world.Auth.RemovePasswordAsync();

        Assert.Null(changed.SettingsError);
        Assert.Equal(AuthState.Open, await world.Auth.GetStateAsync());
        Assert.False(world.Settings.Current.AuthEnabled);
        Assert.Null(await world.Auth.SignInAsync("a good long password"));
    }

    [Fact]
    public async Task The_switch_on_with_no_password_is_a_state_of_its_own()
    {
        // An operator turning it on in the file before ever opening the application,
        // or a container started with it already true. Nobody is locked out, because
        // there is nothing yet to be locked out of.
        await using var world = await AuthWorld.CreateAsync(enabled: true);

        Assert.Equal(AuthState.NeedsPassword, await world.Auth.GetStateAsync());
    }

    [Fact]
    public async Task The_right_password_hands_back_a_stamp_and_the_wrong_one_hands_back_nothing()
    {
        await using var world = await AuthWorld.CreateAsync();

        await world.Auth.SetPasswordAsync("a good long password");

        Assert.NotNull(await world.Auth.SignInAsync("a good long password"));
        Assert.Null(await world.Auth.SignInAsync("a good long passwore"));
    }

    [Fact]
    public async Task Changing_the_password_retires_the_stamp_every_other_device_is_holding()
    {
        await using var world = await AuthWorld.CreateAsync();

        await world.Auth.SetPasswordAsync("the first one");

        var held = await world.Auth.SignInAsync("the first one");
        Assert.NotNull(held);
        Assert.True(await world.Auth.IsStampCurrentAsync(held));

        await world.Auth.SetPasswordAsync("the second one");

        Assert.False(await world.Auth.IsStampCurrentAsync(held));
    }

    [Fact]
    public async Task Removing_the_password_retires_it_too()
    {
        await using var world = await AuthWorld.CreateAsync();

        await world.Auth.SetPasswordAsync("the first one");

        var held = await world.Auth.SignInAsync("the first one");
        await world.Auth.RemovePasswordAsync();

        Assert.False(await world.Auth.IsStampCurrentAsync(held));
    }

    [Fact]
    public async Task A_cookie_carrying_no_stamp_is_never_current()
    {
        await using var world = await AuthWorld.CreateAsync();

        await world.Auth.SetPasswordAsync("a good long password");

        Assert.False(await world.Auth.IsStampCurrentAsync(null));
        Assert.False(await world.Auth.IsStampCurrentAsync(string.Empty));
        Assert.False(await world.Auth.IsStampCurrentAsync("a stamp from somewhere else"));
    }

    [Fact]
    public async Task The_password_is_not_stored_as_the_password()
    {
        await using var world = await AuthWorld.CreateAsync();

        await world.Auth.SetPasswordAsync("a good long password");

        await using var context = world.Database.CreateContext();
        var stored = context.Profiles.Single(p => p.Id == Profile.DefaultProfileId).PasswordHash;

        Assert.NotNull(stored);
        Assert.DoesNotContain("a good long password", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_start_with_the_switch_off_forgets_the_password_it_finds()
    {
        // The way back in after forgetting one: the file is reachable when the pages
        // are the thing locking somebody out.
        await using var world = await AuthWorld.CreateAsync();

        await world.Auth.SetPasswordAsync("a good long password");
        world.Settings.Set(world.Settings.Current with { AuthEnabled = false });

        Assert.True(await world.Auth.ForgetPasswordIfDisabledAsync());
        Assert.Equal(AuthState.Open, await world.Auth.GetStateAsync());
        Assert.Null(await world.Auth.SignInAsync("a good long password"));
    }

    [Fact]
    public async Task A_start_with_the_switch_on_keeps_it()
    {
        await using var world = await AuthWorld.CreateAsync();

        await world.Auth.SetPasswordAsync("a good long password");

        Assert.False(await world.Auth.ForgetPasswordIfDisabledAsync());
        Assert.Equal(AuthState.Locked, await world.Auth.GetStateAsync());
        Assert.NotNull(await world.Auth.SignInAsync("a good long password"));
    }

    [Fact]
    public async Task A_start_with_the_switch_off_and_no_password_has_nothing_to_forget()
    {
        await using var world = await AuthWorld.CreateAsync();

        Assert.False(await world.Auth.ForgetPasswordIfDisabledAsync());
    }

    [Fact]
    public async Task A_settings_file_that_will_not_take_the_switch_says_so()
    {
        // The password is stored either way, and it is the switch that failed — so
        // the application is open while the page reports a password that was set.
        // Saying nothing here would leave somebody believing they had locked it.
        await using var world = await AuthWorld.CreateAsync();
        world.Settings.RefuseWith("/data is read-only");

        var changed = await world.Auth.SetPasswordAsync("a good long password");

        Assert.Equal("/data is read-only", changed.SettingsError);
        Assert.NotNull(changed.Stamp);
        Assert.Equal(AuthState.Open, await world.Auth.GetStateAsync());
    }

    /// <summary>
    /// The service with the two things it reads: a real database, and a settings
    /// file whose <c>Auth:Enabled</c> the options monitor reports live — which is
    /// what production does through the configuration reload.
    /// </summary>
    private sealed class AuthWorld : IAsyncDisposable
    {
        private AuthWorld(SqliteTestDatabase database, FakeSettingsStore settings)
        {
            Database = database;
            Settings = settings;
            Auth = new AuthService(database.ContextFactory, settings, new Monitor(settings));
        }

        public SqliteTestDatabase Database { get; }

        public FakeSettingsStore Settings { get; }

        public AuthService Auth { get; }

        public static async Task<AuthWorld> CreateAsync(bool enabled = false)
        {
            var database = await SqliteTestDatabase.CreateAsync();

            await using (var context = database.CreateContext())
            {
                // The row the initializer creates, stamp included, because that is the
                // state every request runs against.
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

            return new AuthWorld(
                database,
                new FakeSettingsStore(UserSettings.Defaults with { AuthEnabled = enabled }));
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();

        private sealed class Monitor(FakeSettingsStore settings) : IOptionsMonitor<AuthOptions>
        {
            public AuthOptions CurrentValue => new() { Enabled = settings.Current.AuthEnabled };

            public AuthOptions Get(string? name) => CurrentValue;

            public IDisposable? OnChange(Action<AuthOptions, string?> listener) => null;
        }
    }

    /// <summary>A settings file held in memory, which can also refuse a write.</summary>
    private sealed class FakeSettingsStore(UserSettings initial) : IUserSettingsStore
    {
        private string? _refusal;

        public string Path => "userconfig.json";

        public UserSettings Current { get; private set; } = initial;

        public void Set(UserSettings settings) => Current = settings;

        public void RefuseWith(string reason) => _refusal = reason;

        public UserSettings Read() => Current;

        public Task<UserSettingsSaveResult> SaveAsync(
            UserSettings settings,
            CancellationToken cancellationToken = default)
        {
            if (_refusal is not null)
            {
                return Task.FromResult(UserSettingsSaveResult.Failure(Path, _refusal));
            }

            Current = settings;

            return Task.FromResult(UserSettingsSaveResult.Success(Path));
        }

        public Task<bool> EnsureExistsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
