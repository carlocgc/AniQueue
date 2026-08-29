using AniQueue.Core.Domain;
using AniQueue.Core.Settings;
using AniQueue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AniQueue.Infrastructure.Settings;

public sealed class Appearance(IDbContextFactory<AniQueueDbContext> contextFactory) : IAppearance
{
    public async Task<ThemePreference> GetThemeAsync(
        int profileId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var stored = await context.ProfileSettings
            .AsNoTracking()
            .Where(s => s.ProfileId == profileId)
            .Select(s => (ThemePreference?)s.Theme)
            .FirstOrDefaultAsync(cancellationToken);

        return stored ?? ThemePreference.System;
    }

    public async Task SaveThemeAsync(
        int profileId,
        ThemePreference theme,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var settings = await context.ProfileSettings
            .FirstOrDefaultAsync(s => s.ProfileId == profileId, cancellationToken);

        if (settings is null)
        {
            // Nothing has created a settings row for this profile yet. The rest of
            // the defaults come from the entity, which is where they are documented.
            settings = new ProfileSettings { ProfileId = profileId, DisplayName = "AniQueue" };
            context.ProfileSettings.Add(settings);
        }

        settings.Theme = theme;

        await context.SaveChangesAsync(cancellationToken);
    }
}
