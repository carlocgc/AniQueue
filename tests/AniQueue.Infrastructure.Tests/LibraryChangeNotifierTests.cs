using AniQueue.Core.Domain;
using AniQueue.Core.Library;
using AniQueue.Infrastructure.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// What an announcement carries, which is what decides who acts on it.
/// </summary>
public class LibraryChangeNotifierTests
{
    private static LibraryChangeNotifier Notifier() =>
        new(NullLogger<LibraryChangeNotifier>.Instance);

    /// <summary>
    /// A publisher names itself, so its own runner can tell its news from anybody
    /// else's.
    /// </summary>
    /// <remarks>
    /// The whole point of the origin. D41 made every job announce what it changed and
    /// left "do not wake each other in a ring" as a discipline; a job that changes
    /// something on most runs woke its own runner on most runs regardless. Seen for
    /// real: a relation pass that wrote 826 edges produced a second run, triggered by
    /// its own announcement, that found nothing.
    /// </remarks>
    [Fact]
    public void An_announcement_says_who_made_it()
    {
        var notifier = Notifier();
        var heard = new List<LibraryChangeNotification>();

        notifier.Changed += heard.Add;

        notifier.Publish(origin: "relations");

        var only = Assert.Single(heard);

        Assert.Equal("relations", only.Origin);
        Assert.Null(only.Change);
    }

    /// <summary>A page has no origin, so nothing can mistake its news for its own.</summary>
    [Fact]
    public void A_page_publishes_without_naming_a_job()
    {
        var notifier = Notifier();
        var heard = new List<LibraryChangeNotification>();

        notifier.Changed += heard.Add;

        notifier.Publish(new LibraryChange { Source = AnimeSource.MyAnimeList, Created = 2 });

        var only = Assert.Single(heard);

        Assert.Null(only.Origin);
        Assert.Equal(2, only.Change!.Created);
    }

    [Fact]
    public void A_handler_that_throws_does_not_stop_the_others()
    {
        var notifier = Notifier();
        var heard = 0;

        notifier.Changed += _ => throw new InvalidOperationException("a page gave up");
        notifier.Changed += _ => heard++;

        notifier.Publish(origin: "sync");

        Assert.Equal(1, heard);
    }

    [Fact]
    public void Nothing_is_delivered_once_a_handler_has_gone()
    {
        var notifier = Notifier();
        var heard = 0;

        void Handler(LibraryChangeNotification _) => heard++;

        notifier.Changed += Handler;
        notifier.Changed -= Handler;

        notifier.Publish(origin: "sync");

        Assert.Equal(0, heard);
    }
}
