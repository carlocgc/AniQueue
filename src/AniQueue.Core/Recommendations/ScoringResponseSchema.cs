namespace AniQueue.Core.Recommendations;

/// <summary>
/// The response shape as a JSON Schema, for a server that can constrain its model.
/// </summary>
/// <remarks>
/// In Core beside <see cref="ScoringPromptBuilder"/> and
/// <see cref="ScoringResponseParser"/> for the reason they are: what a model is told
/// to return, what a server is told to enforce, and what AniQueue will accept are
/// three statements of one thing, and keeping them apart is how they come to disagree.
///
/// <b>Shape only, deliberately.</b> No minimums, no maximums, no uniqueness. Servers
/// convert this to a grammar and the conversions vary in what they support, so a
/// schema that expresses everything is a schema some servers refuse — and the
/// constraints left out are exactly the ones the parser already enforces and tests.
/// The wire schema stops a model returning prose; the parser decides whether the
/// numbers mean anything (D31).
///
/// <b>The envelope is not required here</b> even though the prompt asks for it.
/// <see cref="ScoringResponseParser"/> tolerates its absence on purpose — models
/// return the array reliably and the wrapper unreliably — so requiring it on the wire
/// would make a server refuse replies AniQueue would have accepted.
/// </remarks>
public static class ScoringResponseSchema
{
    /// <summary>The name servers attach to the schema. Cosmetic, and required by some.</summary>
    public const string Name = "aniqueue_scoring_response";

    /// <summary>The schema itself, as JSON.</summary>
    public const string Json =
        """
        {
          "type": "object",
          "properties": {
            "results": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "integer" },
                  "rank": { "type": "integer" },
                  "predictedScore": { "type": "number" },
                  "confidence": { "type": "number" },
                  "reason": { "type": "string" }
                },
                "required": ["id", "rank", "predictedScore", "confidence"]
              }
            }
          },
          "required": ["results"]
        }
        """;
}
