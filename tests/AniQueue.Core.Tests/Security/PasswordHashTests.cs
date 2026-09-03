using System.Globalization;
using AniQueue.Core.Security;

namespace AniQueue.Core.Tests.Security;

public class PasswordHashTests
{
    [Fact]
    public void A_password_verifies_against_its_own_hash()
    {
        var stored = PasswordHash.Create("correct horse battery staple");

        Assert.True(PasswordHash.Verify(stored, "correct horse battery staple"));
    }

    [Fact]
    public void A_different_password_does_not()
    {
        var stored = PasswordHash.Create("correct horse battery staple");

        Assert.False(PasswordHash.Verify(stored, "correct horse battery stapl"));
    }

    [Fact]
    public void The_comparison_is_case_sensitive()
    {
        var stored = PasswordHash.Create("Kimagure Orange Road");

        Assert.False(PasswordHash.Verify(stored, "kimagure orange road"));
    }

    [Fact]
    public void The_same_password_hashes_differently_every_time()
    {
        // The salt, which is what stops one stolen database answering the question
        // "does anybody here use this password" in a single comparison.
        var first = PasswordHash.Create("shared");
        var second = PasswordHash.Create("shared");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHash.Verify(first, "shared"));
        Assert.True(PasswordHash.Verify(second, "shared"));
    }

    [Fact]
    public void A_stored_value_carries_the_work_factor_it_was_made_with()
    {
        // So that raising the cost later still verifies a password hashed before the
        // change, rather than locking out the one account this application has.
        var stored = PasswordHash.Create("kept");

        var parts = stored.Split('.');

        Assert.Equal(4, parts.Length);
        Assert.Equal("v1", parts[0]);
        Assert.True(int.Parse(parts[1], CultureInfo.InvariantCulture) >= 210_000);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a hash")]
    [InlineData("v1.210000.notbase64.notbase64")]
    [InlineData("v2.210000.c2FsdA==.aGFzaA==")]
    [InlineData("v1.zero.c2FsdA==.aGFzaA==")]
    [InlineData("v1.0.c2FsdA==.aGFzaA==")]
    public void An_unreadable_stored_value_refuses_rather_than_throws(string stored)
    {
        // A column holding something this build cannot read is a locked application,
        // not a crashed one. The settings file is what clears it.
        Assert.False(PasswordHash.Verify(stored, "anything"));
    }

    [Fact]
    public void Nothing_verifies_against_no_password()
    {
        Assert.False(PasswordHash.Verify(null, "anything"));
        Assert.False(PasswordHash.Verify(PasswordHash.Create("set"), null));
        Assert.False(PasswordHash.Verify(PasswordHash.Create("set"), string.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_password_is_refused_rather_than_hashed(string password)
    {
        // Otherwise clearing the box and pressing the button would lock the
        // application behind a password of nothing.
        Assert.Throws<ArgumentException>(() => PasswordHash.Create(password));
    }
}
