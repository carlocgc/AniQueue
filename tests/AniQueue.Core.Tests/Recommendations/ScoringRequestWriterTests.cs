using System.Text.Json;
using AniQueue.Core.Domain;
using AniQueue.Core.Recommendations;

namespace AniQueue.Core.Tests.Recommendations;

/// <summary>
/// What a model is given. These assert the shape on the wire rather than the
/// records behind it, because the shape is the contract: a user pastes this into a
/// prompt and a response is matched back against it, so a field quietly renamed by
/// a refactor is a broken round trip rather than a broken test.
/// </summary>
public class ScoringRequestWriterTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 20, 9, 30, 0, TimeSpan.Zero);

    private static JsonElement WriteAndRead(ScoringRequest request) =>
        JsonDocument.Parse(ScoringRequestWriter.Write(request)).RootElement.Clone();

    private static ScoringRequest Minimal(params ScoringCandidate[] candidates) => new()
    {
        GeneratedAt = When,
        Candidates = candidates
    };

    [Fact]
    public void Declares_its_format_and_version()
    {
        var root = WriteAndRead(Minimal());
        var envelope = root.GetProperty("aniqueue");

        Assert.Equal("aniqueue-scoring-request", envelope.GetProperty("format").GetString());
        Assert.Equal(1, envelope.GetProperty("version").GetInt32());
    }

    [Fact]
    public void States_the_scale_scores_are_on()
    {
        var scale = WriteAndRead(Minimal()).GetProperty("scale");

        Assert.Equal(1, scale.GetProperty("min").GetInt32());
        Assert.Equal(10, scale.GetProperty("max").GetInt32());
    }

    [Fact]
    public void Writes_a_candidate_with_everything_known_about_it()
    {
        var root = WriteAndRead(Minimal(new ScoringCandidate
        {
            Id = 412,
            Title = "Steins;Gate",
            Titles = new ScoringCandidateTitles { Romaji = "Steins;Gate", English = "Steins Gate" },
            MediaType = MediaType.Tv,
            Episodes = 24,
            EpisodeMinutes = 24,
            Year = 2011,
            ExternalIds = new ScoringCandidateIds { AniList = "9253", MyAnimeList = "9253" }
        }));

        var candidate = root.GetProperty("candidates")[0];

        Assert.Equal(412, candidate.GetProperty("id").GetInt32());
        Assert.Equal("Steins;Gate", candidate.GetProperty("title").GetString());
        Assert.Equal("Tv", candidate.GetProperty("mediaType").GetString());
        Assert.Equal(24, candidate.GetProperty("episodes").GetInt32());
        Assert.Equal(24, candidate.GetProperty("episodeMinutes").GetInt32());
        Assert.Equal(2011, candidate.GetProperty("year").GetInt32());
        Assert.Equal("Steins Gate", candidate.GetProperty("titles").GetProperty("english").GetString());
        Assert.Equal("9253", candidate.GetProperty("externalIds").GetProperty("anilist").GetString());
    }

    [Fact]
    public void Omits_what_is_unknown_rather_than_writing_null()
    {
        // A backlog is mostly optional metadata, and on a small model every null is
        // context spent saying nothing.
        var candidate = WriteAndRead(Minimal(new ScoringCandidate
        {
            Id = 1,
            Title = "A film nobody catalogued"
        })).GetProperty("candidates")[0];

        Assert.False(candidate.TryGetProperty("episodes", out _));
        Assert.False(candidate.TryGetProperty("episodeMinutes", out _));
        Assert.False(candidate.TryGetProperty("year", out _));
        Assert.False(candidate.TryGetProperty("titles", out _));
        Assert.False(candidate.TryGetProperty("externalIds", out _));
        Assert.False(candidate.TryGetProperty("notes", out _));
    }

    [Fact]
    public void Omits_an_unknown_media_type_rather_than_asserting_one()
    {
        var candidate = WriteAndRead(Minimal(new ScoringCandidate
        {
            Id = 1,
            Title = "Provenance unknown",
            MediaType = MediaType.Unknown
        })).GetProperty("candidates")[0];

        Assert.False(candidate.TryGetProperty("mediaType", out _));
    }

    [Fact]
    public void Writes_notes_when_they_were_opted_in()
    {
        var candidate = WriteAndRead(Minimal(new ScoringCandidate
        {
            Id = 1,
            Title = "Recommended by a friend",
            Notes = "Ben says start here"
        })).GetProperty("candidates")[0];

        Assert.Equal("Ben says start here", candidate.GetProperty("notes").GetString());
    }

    [Fact]
    public void Says_how_much_history_exists_even_when_none_was_capped()
    {
        var root = WriteAndRead(new ScoringRequest
        {
            GeneratedAt = When,
            HistoryAvailable = 2,
            History =
            [
                new ScoringHistoryEntry { Title = "Cowboy Bebop", Score = 9, MediaType = MediaType.Tv, Year = 1998 },
                new ScoringHistoryEntry { Title = "Akira", Score = 7, MediaType = MediaType.Movie, Year = 1988 }
            ]
        });

        Assert.Equal(2, root.GetProperty("historyAvailable").GetInt32());

        var first = root.GetProperty("history")[0];
        Assert.Equal("Cowboy Bebop", first.GetProperty("title").GetString());
        Assert.Equal(9, first.GetProperty("score").GetInt32());
    }

    [Fact]
    public void Reports_a_capped_history_as_capped()
    {
        var request = new ScoringRequest
        {
            GeneratedAt = When,
            HistoryAvailable = 566,
            History = [new ScoringHistoryEntry { Title = "Cowboy Bebop", Score = 9 }]
        };

        Assert.True(request.IsHistoryCapped);
        Assert.Equal(566, WriteAndRead(request).GetProperty("historyAvailable").GetInt32());
    }

    [Fact]
    public void Escapes_a_title_that_would_otherwise_break_the_document()
    {
        // Native titles and quotation marks in one payload, read back through a real
        // parser rather than inspected as text.
        var candidate = WriteAndRead(Minimal(new ScoringCandidate
        {
            Id = 1,
            Title = "\"Bakemonogatari\" — 化物語",
            Titles = new ScoringCandidateTitles { Native = "化物語" }
        })).GetProperty("candidates")[0];

        Assert.Equal("\"Bakemonogatari\" — 化物語", candidate.GetProperty("title").GetString());
        Assert.Equal("化物語", candidate.GetProperty("titles").GetProperty("native").GetString());
    }
}
