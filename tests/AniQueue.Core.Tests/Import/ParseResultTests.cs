using AniQueue.Core.Domain;
using AniQueue.Core.Import;

namespace AniQueue.Core.Tests.Import;

/// <summary>
/// Merging the parts of one fetch (§5).
///
/// A sync may receive its list in several responses, and the preview takes one
/// <see cref="ParseResult"/> — so this is the join, and the rules it enforces are
/// the difference between a paging failure and a mass deletion.
/// </summary>
public class ParseResultTests
{
    private static ParseResult Part(params string[] aniListIds) => new()
    {
        Entries = [.. aniListIds.Select(id => new ParsedLibraryEntry
        {
            Source = AnimeSource.AniList,
            ExternalIds = [new ExternalIdentifier(AnimeSource.AniList, id)],
            Title = $"Title {id}"
        })],
        Problems = []
    };

    [Fact]
    public void Entries_and_problems_from_every_part_arrive()
    {
        var merged = ParseResult.Merge([Part("1", "2"), Part("3")]);

        Assert.False(merged.IsFileRejected);
        Assert.Equal(3, merged.Entries.Count);
    }

    [Fact]
    public void One_rejected_part_rejects_the_whole_fetch()
    {
        // Four responses of which three could be read is not three-quarters of a
        // library; it is a library with a quarter missing, and absence is what a
        // sync is entitled to act on (D19).
        var merged = ParseResult.Merge([Part("1", "2"), ParseResult.Rejected("truncated")]);

        Assert.True(merged.IsFileRejected);
        Assert.Empty(merged.Entries);
        Assert.Contains(merged.Problems, p => p.Message.Contains("truncated", StringComparison.Ordinal));
    }

    [Fact]
    public void An_entry_repeated_across_parts_is_kept_once()
    {
        // Within one payload a repeated identifier is a real contradiction and the
        // preview surfaces it as a conflict. Across payloads it is an artifact of
        // how the list was chunked, and asking the user to resolve several hundred
        // of those would be the pipeline blaming them for its own paging.
        var merged = ParseResult.Merge([Part("1", "2"), Part("2", "3")]);

        Assert.Equal(3, merged.Entries.Count);
        Assert.Equal(["1", "2", "3"], merged.Entries.Select(e => e.ExternalIds[0].Value));
    }

    [Fact]
    public void A_dropped_duplicate_does_not_claim_identifiers_for_anything_else()
    {
        // The dropped entry carries two identifiers, one of which is new. Claiming
        // it on the way out would reject the next entry that legitimately holds it.
        var first = Part("1");
        var second = new ParseResult
        {
            Entries =
            [
                new ParsedLibraryEntry
                {
                    Source = AnimeSource.AniList,
                    ExternalIds =
                    [
                        new ExternalIdentifier(AnimeSource.AniList, "1"),
                        new ExternalIdentifier(AnimeSource.MyAnimeList, "99")
                    ],
                    Title = "Duplicate"
                },
                new ParsedLibraryEntry
                {
                    Source = AnimeSource.MyAnimeList,
                    ExternalIds = [new ExternalIdentifier(AnimeSource.MyAnimeList, "99")],
                    Title = "A different title that really holds it"
                }
            ],
            Problems = []
        };

        var merged = ParseResult.Merge([first, second]);

        Assert.Equal(2, merged.Entries.Count);
        Assert.Contains(merged.Entries, e => e.Title == "A different title that really holds it");
    }

    [Fact]
    public void Merging_nothing_is_an_empty_result_rather_than_a_rejection()
    {
        var merged = ParseResult.Merge([]);

        Assert.False(merged.IsFileRejected);
        Assert.Empty(merged.Entries);
    }
}
