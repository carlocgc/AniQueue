using System.Net;
using System.Net.Http.Headers;
using AniQueue.Core.Artwork;
using AniQueue.Infrastructure.Artwork;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// The fetch guards, against a stub transport.
/// </summary>
/// <remarks>
/// Which addresses are acceptable is decided in Core and tested there exhaustively.
/// What is left here is everything that needs a response to exist before it can be
/// judged — a status, a content type, a length, a redirect — and the one thing no
/// pure test can check: that a refused address is never actually requested.
/// </remarks>
public class CoverArtClientTests
{
    private const string Allowed = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/small/bx1-abc.jpg";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // A real handler observes the token before doing anything, and a stub that
            // does not would let a cancelled pass appear to succeed — hiding exactly
            // the behaviour the cancellation test exists to check.
            cancellationToken.ThrowIfCancellationRequested();

            Requests++;
            LastRequest = request;

            return Task.FromResult(respond(request));
        }
    }

    private static (CoverArtClient Client, StubHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);

        return (
            new CoverArtClient(new HttpClient(handler), NullLogger<CoverArtClient>.Instance),
            handler);
    }

    private static HttpResponseMessage Image(
        byte[] content,
        string contentType = "image/jpeg",
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(content) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        return response;
    }

    [Fact]
    public async Task A_picture_that_arrives_carries_the_extension_its_type_implies()
    {
        var (client, _) = Build(_ => Image([1, 2, 3], "image/png"));

        var fetch = await client.FetchAsync(Allowed, CancellationToken.None);

        Assert.Equal(CoverArtFetchStatus.Fetched, fetch.Status);
        Assert.Equal(".png", fetch.FileExtension);
        Assert.Equal([1, 2, 3], fetch.Content);
    }

    [Fact]
    public async Task An_address_that_is_not_allowed_is_never_requested()
    {
        // The point of the allowlist is that no socket opens, so asserting on the
        // status alone would pass even if the request had been made and refused
        // afterwards.
        var (client, handler) = Build(_ => Image([1]));

        var fetch = await client.FetchAsync(
            "https://anilist.co.example.invalid/cover.jpg", CancellationToken.None);

        Assert.Equal(CoverArtFetchStatus.PermanentlyUnavailable, fetch.Status);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task A_redirect_is_refused_rather_than_followed()
    {
        // The allowlist vouches for the host that was asked for. Following a redirect
        // would let that host nominate any other, which is the whole surface the
        // allowlist exists to close.
        var (client, handler) = Build(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://example.invalid/elsewhere.jpg");
            return response;
        });

        var fetch = await client.FetchAsync(Allowed, CancellationToken.None);

        Assert.Equal(CoverArtFetchStatus.PermanentlyUnavailable, fetch.Status);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData("image/svg+xml")]
    public async Task A_body_that_is_not_a_picture_is_permanently_unavailable(string contentType)
    {
        var (client, _) = Build(_ => Image([1, 2, 3], contentType));

        var fetch = await client.FetchAsync(Allowed, CancellationToken.None);

        Assert.Equal(CoverArtFetchStatus.PermanentlyUnavailable, fetch.Status);
    }

    [Fact]
    public async Task A_body_over_the_cap_is_permanently_unavailable()
    {
        var (client, _) = Build(_ => Image(new byte[ImageSource.MaxByteCount + 1]));

        var fetch = await client.FetchAsync(Allowed, CancellationToken.None);

        Assert.Equal(CoverArtFetchStatus.PermanentlyUnavailable, fetch.Status);
    }

    [Fact]
    public async Task An_empty_body_is_permanently_unavailable()
    {
        // Otherwise a zero-byte file is cached under an address that, being
        // immutable, is never looked at again.
        var (client, _) = Build(_ => Image([]));

        var fetch = await client.FetchAsync(Allowed, CancellationToken.None);

        Assert.Equal(CoverArtFetchStatus.PermanentlyUnavailable, fetch.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_client_error_is_about_this_address_and_is_permanent(HttpStatusCode status)
    {
        var (client, _) = Build(_ => new HttpResponseMessage(status));

        var fetch = await client.FetchAsync(Allowed, CancellationToken.None);

        Assert.Equal(CoverArtFetchStatus.PermanentlyUnavailable, fetch.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_server_having_a_moment_is_transient(HttpStatusCode status)
    {
        var (client, _) = Build(_ => new HttpResponseMessage(status));

        var fetch = await client.FetchAsync(Allowed, CancellationToken.None);

        Assert.Equal(CoverArtFetchStatus.TemporarilyUnavailable, fetch.Status);
    }

    [Fact]
    public async Task A_dropped_connection_is_transient()
    {
        var (client, _) = Build(_ => throw new HttpRequestException("no route to host"));

        var fetch = await client.FetchAsync(Allowed, CancellationToken.None);

        Assert.Equal(CoverArtFetchStatus.TemporarilyUnavailable, fetch.Status);
    }

    [Fact]
    public async Task A_cancelled_fetch_throws_rather_than_reporting_a_failure()
    {
        // Cancelling has to cost the title nothing. Reported as a transient failure
        // it would spend one of five attempts, so pressing Cancel five times would
        // permanently blank whatever was in flight.
        var (client, _) = Build(_ => Image([1]));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.FetchAsync(Allowed, cancelled.Token));
    }
}
