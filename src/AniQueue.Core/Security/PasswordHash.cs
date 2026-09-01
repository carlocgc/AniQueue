using System.Globalization;
using System.Security.Cryptography;

namespace AniQueue.Core.Security;

/// <summary>
/// Turns a password into a value that can be stored, and checks a password
/// against one.
/// </summary>
/// <remarks>
/// PBKDF2 over HMAC-SHA512 from the base class library, which is the construction
/// ASP.NET Core's own <c>PasswordHasher</c> performs. It is here rather than there
/// because reaching that type means giving a reference to ASP.NET to a project
/// with none, and this project's isolation is what keeps its tests fast.
///
/// The stored value carries its own parameters, so raising
/// <see cref="Iterations"/> later still verifies every password hashed before the
/// change. Nothing rehashes on sign-in: a single-user application has one password
/// and changing it is the moment the newer cost applies.
/// </remarks>
public static class PasswordHash
{
    /// <summary>
    /// Work factor, at OWASP's 2023 guidance for PBKDF2-HMAC-SHA512.
    /// </summary>
    /// <remarks>
    /// It is also half of what makes guessing expensive; the other half is the
    /// pause a wrong password costs, which lives with the sign-in rather than here.
    /// </remarks>
    private const int Iterations = 210_000;

    private const int SaltBytes = 16;

    private const int HashBytes = 32;

    /// <summary>Names the format, so a later one can be told apart rather than misread.</summary>
    private const string Version = "v1";

    private const char Separator = '.';

    /// <summary>Hashes a password with a fresh salt.</summary>
    public static string Create(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);

        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA512,
            HashBytes);

        return string.Join(
            Separator,
            Version,
            Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    /// <summary>
    /// Whether <paramref name="password"/> is the one <paramref name="stored"/> was
    /// made from. False for anything it cannot read, rather than an exception:
    /// a column holding a value from a build that no longer exists is a locked
    /// application, not a crashed one, and the file clears it.
    /// </summary>
    public static bool Verify(string? stored, string? password)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var parts = stored.Split(Separator);

        if (parts.Length != 4 || parts[0] != Version)
        {
            return false;
        }

        if (!int.TryParse(parts[1], CultureInfo.InvariantCulture, out var iterations) || iterations < 1)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA512,
            expected.Length);

        // Fixed-time, because a comparison that stops at the first differing byte
        // reports how much of a guess was right in the time it takes to answer.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
