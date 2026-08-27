using System.Net;
using System.Text.Json;
using AniQueue.Core.Domain;
using AniQueue.Core.Recommendations;
using AniQueue.Infrastructure.Persistence;
using AniQueue.Infrastructure.Recommendations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// Phase 8b's exit criterion, whole: a request built from a library, carried to an
/// endpoint that is not there, and the reply turned into a preview a person could
/// apply — with no page anywhere in it.
/// </summary>
/// <remarks>
/// <b>This is the test D31's claim rests on.</b> The manual path and a hosted endpoint
/// are supposed to be one contract carried by two couriers, and the only way to know
/// that is true is to run the second courier into the same
/// <see cref="IRecommendationService.PreviewAsync"/> the first one uses and find the
/// same kind of answer coming out. If this needed its own preview type, or its own
/// validation, the second courier would have become a second pipeline.
/// </remarks>
public class ScoringCourierTests : IAsyncDisposable
{
    private sealed class StubHandler(Func<string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        model = "qwen2.5-14b",
                        choices = new[]
                        {
                            new { message = new { role = "assistant", content = reply() }, finish_reason = "stop" }
                        }
                    }),
                    System.Text.Encoding.UTF8,
                    "application/json")
            });
    }

    private SqliteTestDatabase _database = null!;

    private async Task<(IRecommendationService Service, int ProfileId)> CreateAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();

        await new DatabaseInitializer(
            _database.ContextFactory,
            Options.Create(new AniQueueDatabaseOptions { Path = ":memory:" }),
            NullLogger<DatabaseInitializer>.Instance).InitialiseAsync();

        await using var context = _database.CreateContext();
        var profile = await context.Profiles.FirstAsync();

        return (
            new RecommendationService(
                _database.ContextFactory,
                new ScoringResponseParser(),
                NullLogger<RecommendationService>.Instance),
            profile.Id);
    }

    private static IScoringEndpoint Endpoint(Func<string> reply) =>
        new ChatCompletionsEndpoint(
            new HttpClient(new StubHandler(reply)),
            new StaticOptionsMonitor<ScoringOptions>(new ScoringOptions
            {
                Endpoint = "http://192.168.1.50:1234",
                Model = "qwen2.5-14b"
            }),
            NullLogger<ChatCompletionsEndpoint>.Instance);

    private async Task<Anime> PlanAsync(int profileId, string title)
    {
        await using var context = _database.CreateContext();

        var anime = new Anime { Title = title, MediaType = MediaType.Tv, Source = AnimeSource.AniList };
        context.Anime.Add(anime);
        await context.SaveChangesAsync();

        context.LibraryEntries.Add(new LibraryEntry
        {
            ProfileId = profileId,
            AnimeId = anime.Id,
            Status = LibraryStatus.Planning,
            DateAdded = DateTimeOffset.UtcNow,
            LastUpdated = DateTimeOffset.UtcNow
        });

        await context.SaveChangesAsync();

        return anime;
    }

    [Fact]
    public async Task A_configured_endpoint_produces_a_preview_that_can_be_applied()
    {
        var (service, profileId) = await CreateAsync();

        var first = await PlanAsync(profileId, "Hinamatsuri");
        var second = await PlanAsync(profileId, "Dragon Maid");

        var request = await service.BuildRequestAsync(profileId);

        var endpoint = Endpoint(() => $$"""
            {
              "aniqueue": { "format": "aniqueue-scoring-response", "version": 1 },
              "results": [
                { "id": {{first.Id}}, "predictedScore": 8.4, "confidence": 0.79,
                  "reason": "Deadpan comedy, like the ones you rate highest." },
                { "id": {{second.Id}}, "predictedScore": 7.1, "confidence": 0.6 }
              ]
            }
            """);

        var answer = await endpoint.AskAsync(request);
        Assert.True(answer.Succeeded);

        // The same method the paste box calls, given the same kind of string. Nothing
        // about this preview knows which courier produced it, and that is the point.
        //
        // It is told the route, which is not the same thing (D50): what differs is
        // whether a person carried the document, not which server answered.
        var preview = await service.PreviewAsync(profileId, ScoringRoute.Endpoint, answer.Reply!, request);

        Assert.False(preview.HasErrors);
        Assert.True(preview.CanApply);
        Assert.Equal(2, preview.ApplicableCount);

        var applied = await service.ApplyAsync(profileId, preview, "Remote", answer.ModelIdentifier);

        Assert.Equal(2, applied.Applied);

        await using var context = _database.CreateContext();
        var entry = await context.LibraryEntries.SingleAsync(e => e.AnimeId == first.Id);

        Assert.Equal(8.4, entry.RecommendationScore);
        Assert.Equal("Deadpan comedy, like the ones you rate highest.", entry.RecommendationReason);

        // The run records how it was carried and what answered, which is what lets
        // somebody decide later whether a better model is worth re-running for.
        var run = await context.RecommendationRuns.SingleAsync();

        Assert.Equal("Remote", run.ProviderName);
        Assert.Equal("qwen2.5-14b", run.ModelIdentifier);
    }

    [Fact]
    public async Task A_fenced_reply_from_an_endpoint_still_becomes_a_preview()
    {
        // The case the scheduled sweep lives or dies on, end to end: the model wrapped
        // its answer, nobody was there to unwrap it, and the ranking still lands.
        var (service, profileId) = await CreateAsync();
        var anime = await PlanAsync(profileId, "Hinamatsuri");

        var request = await service.BuildRequestAsync(profileId);

        var endpoint = Endpoint(() => $$"""
            Sure! Here is the ranking:
            ```json
            { "results": [{ "id": {{anime.Id}}, "predictedScore": 8.4, "confidence": 0.8 }] }
            ```
            """);

        var answer = await endpoint.AskAsync(request);
        var preview = await service.PreviewAsync(profileId, ScoringRoute.Endpoint, answer.Reply!, request);

        Assert.True(preview.CanApply);

        // And it says what it threw away, so the score stays auditable (D37).
        Assert.Contains(preview.Problems, p => p.Severity == ScoringSeverity.Warning);
    }

    [Fact]
    public async Task A_reply_that_ranks_a_title_the_library_does_not_have_is_refused()
    {
        // Validation is not relaxed because the reply arrived over HTTP rather than
        // through a paste box. The endpoint carries; the service still decides.
        var (service, profileId) = await CreateAsync();
        await PlanAsync(profileId, "Hinamatsuri");

        var request = await service.BuildRequestAsync(profileId);

        var endpoint = Endpoint(() =>
            """{ "results": [{ "id": 9999, "predictedScore": 8.0, "confidence": 0.7 }] }""");

        var answer = await endpoint.AskAsync(request);
        var preview = await service.PreviewAsync(profileId, ScoringRoute.Endpoint, answer.Reply!, request);

        Assert.True(preview.HasErrors);
        Assert.False(preview.CanApply);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyAsync(profileId, preview, "Remote"));
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}
