namespace AniQueue.Core.Recommendations;

/// <summary>
/// How much a problem with a response matters.
/// </summary>
/// <remarks>
/// The split is the whole reason this feature is usable on a model small enough to
/// self-host, and it is a narrower rule than "reject anything imperfect".
///
/// An <see cref="Error"/> means the response cannot be trusted as a statement
/// about this library: a title that repeats, a score off the scale, an id naming
/// nothing. There is no honest way to apply part of that, so nothing is applied —
/// which is what "never applied in part" (D31) protects, because a half-applied
/// ranking is indistinguishable from a complete one an hour later.
///
/// A <see cref="Warning"/> means the response is a valid ranking that says less
/// than was asked for. A model that ranks 170 of 182 candidates has answered
/// correctly about 170 titles, and discarding those because of the twelve it
/// omitted would be strictness that costs the user everything and protects
/// nothing.
/// </remarks>
public enum ScoringSeverity
{
    /// <summary>Reported, and the ranking still applies.</summary>
    Warning = 0,

    /// <summary>Nothing is applied.</summary>
    Error = 1
}

/// <summary>Something wrong with a response, or with one result inside it.</summary>
/// <param name="Message">Phrased for the person who has to decide what to do about it.</param>
/// <param name="Severity">Whether it stops the ranking being applied.</param>
public sealed record ScoringProblem(string Message, ScoringSeverity Severity)
{
    public static ScoringProblem Error(string message) => new(message, ScoringSeverity.Error);

    public static ScoringProblem Warning(string message) => new(message, ScoringSeverity.Warning);

    public override string ToString() => Message;
}
