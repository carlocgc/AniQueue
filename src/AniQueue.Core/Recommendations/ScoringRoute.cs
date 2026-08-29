namespace AniQueue.Core.Recommendations;

/// <summary>
/// How a reply reached AniQueue, which decides how much it has to prove about
/// itself.
/// </summary>
/// <remarks>
/// The two routes differ in one way that matters to validation: whether a person
/// carried the document.
///
/// It is stated by the caller rather than inferred, and it has no default. A
/// defaulted value would be a silent answer to the only question in this method
/// that decides whether a wrong reply is refused, and the caller always knows.
/// </remarks>
public enum ScoringRoute
{
    /// <summary>
    /// A reply a person pasted, uploaded, or otherwise carried in by hand.
    /// </summary>
    /// <remarks>
    /// The only route on which the wrong document can arrive, and so the only one
    /// that requires a reply to name the database it was built against. AniQueue
    /// cannot check a file it did not produce by any other means: every id inside it
    /// is a row key, and a row key from somewhere else is not wrong-looking, it is
    /// simply about something else.
    /// </remarks>
    Pasted = 0,

    /// <summary>
    /// A reply a configured endpoint returned, in answer to a request AniQueue sent
    /// moments earlier.
    /// </summary>
    /// <remarks>
    /// Exempt from the database check, and the exemption is structural rather than a
    /// concession. The request was built and the answer received inside one process,
    /// so there is no document to mix up and nothing a key could establish that the
    /// call stack does not already.
    ///
    /// It is also the route that cannot supply one: <see cref="ScoringResponseSchema"/>
    /// deliberately declares no envelope, because requiring it on the wire made
    /// servers refuse replies AniQueue would have accepted — so a model constrained
    /// to that schema returns the results array and nothing around it.
    /// </remarks>
    Endpoint = 1
}
