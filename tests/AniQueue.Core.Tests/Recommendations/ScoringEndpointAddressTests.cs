using AniQueue.Core.Recommendations;

namespace AniQueue.Core.Tests.Recommendations;

/// <summary>
/// The guards: what AniQueue will and will not be told to POST to.
/// </summary>
/// <remarks>
/// Every other outbound endpoint is a constant in code, so there is no
/// request-forgery surface. This one is settable from a page, and these are what
/// replaced that protection — so the cases that must be refused matter more here than
/// the ones that must work.
/// </remarks>
public class ScoringEndpointAddressTests
{
    private static (bool Ok, Uri? Target, string? Refusal) Resolve(string? endpoint)
    {
        var ok = ScoringEndpointAddress.TryResolve(endpoint, out var target, out var refusal);
        return (ok, target, refusal);
    }

    [Theory]
    [InlineData("http://192.168.1.50:1234")]
    [InlineData("http://localhost:11434")]
    [InlineData("https://model.lan")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://10.0.0.4:1234")]
    public void The_addresses_a_self_hosted_model_actually_lives_at_are_allowed(string endpoint)
    {
        // Loopback and private ranges are permitted deliberately. Refusing them would be
        // theatre: reaching the page at all means already being on that network, so the
        // forgery grants no reach the caller did not have.
        var (ok, target, _) = Resolve(endpoint);

        Assert.True(ok);
        Assert.Equal(ScoringEndpointAddress.CompletionsPath, target!.AbsolutePath);
    }

    [Fact]
    public void A_bare_host_and_port_is_completed_rather_than_refused()
    {
        // What somebody reads off LM Studio's own screen.
        var (ok, target, _) = Resolve("192.168.1.50:1234");

        Assert.True(ok);
        Assert.Equal("http://192.168.1.50:1234/v1/chat/completions", target!.ToString());
    }

    [Fact]
    public void The_path_is_ours_whatever_the_operator_pasted()
    {
        // A full URL copied from documentation would otherwise be concatenated into
        // something that 404s, and the 404 would look like the model's fault.
        var (ok, target, _) = Resolve("http://192.168.1.50:1234/v1/chat/completions");

        Assert.True(ok);
        Assert.Equal("http://192.168.1.50:1234/v1/chat/completions", target!.ToString());
    }

    [Fact]
    public void A_trailing_slash_does_not_double_up()
    {
        var (ok, target, _) = Resolve("http://192.168.1.50:1234/");

        Assert.True(ok);
        Assert.Equal("http://192.168.1.50:1234/v1/chat/completions", target!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Nothing_configured_is_refused_without_pretending_it_is_an_error(string? endpoint)
    {
        var (ok, _, refusal) = Resolve(endpoint);

        Assert.False(ok);
        Assert.Contains("No model endpoint", refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://192.168.1.50:70")]
    [InlineData("ftp://192.168.1.50")]
    public void Only_http_and_https_are_allowed(string endpoint)
    {
        var (ok, _, refusal) = Resolve(endpoint);

        Assert.False(ok);
        Assert.Contains("http", refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://169.254.169.254:80/latest/meta-data")]
    [InlineData("http://169.254.1.1:1234")]
    public void Link_local_is_refused_because_it_is_where_credentials_live(string endpoint)
    {
        // The one address in this picture that reaches something genuinely privileged,
        // and one no model has ever been served from. On a hosted machine it is the
        // metadata service, handing out credentials to anyone who asks.
        var (ok, _, refusal) = Resolve(endpoint);

        Assert.False(ok);
        Assert.Contains("link-local", refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Credentials_in_the_address_are_refused_rather_than_stripped()
    {
        // Stripping would send the request to the host anyway and silently drop the
        // half the operator thought was doing something.
        var (ok, _, refusal) = Resolve("http://user:secret@192.168.1.50:1234");

        Assert.False(ok);
        Assert.Contains("username or password", refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Something_that_is_not_an_address_says_so()
    {
        var (ok, _, refusal) = Resolve("not a url at all");

        Assert.False(ok);
        Assert.Contains("not an address", refusal!, StringComparison.OrdinalIgnoreCase);
    }
}
