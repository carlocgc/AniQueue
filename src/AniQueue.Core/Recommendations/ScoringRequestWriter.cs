using System.Buffers;
using System.Text;
using System.Text.Json;
using AniQueue.Core.Domain;

namespace AniQueue.Core.Recommendations;

/// <summary>
/// Writes a <see cref="ScoringRequest"/> as the JSON a model is given.
/// </summary>
/// <remarks>
/// Hand-written with <see cref="Utf8JsonWriter"/> rather than reflected over the
/// records, because the shape on the wire is the contract and the records are only
/// how this build happens to hold it. Serialising the objects would let a renamed
/// property silently rename a field a user has already pasted into a prompt, and
/// would emit every null a title happens to have — which on a payload that is
/// mostly optional metadata is noise a small model pays for in context.
///
/// Absent means absent: a property with no value is not written at all rather than
/// written as null. There is no difference between the two for a reader, and one
/// of them is shorter.
///
/// Field order is load-bearing, and only for one reason: the server's prompt cache.
/// A sweep sends the same history — around twenty thousand tokens of it — with every
/// batch, and a local server reuses the state it computed for an identical prefix
/// rather than processing those tokens again. That reuse ends at the first byte that
/// differs, so everything invariant is written before <c>history</c> and everything
/// that changes between batches is written after it. Nothing about the order is a
/// hint to the model; a reader cannot tell the difference.
///
/// Field order is necessary and not sufficient. Whether the tokens are actually
/// reused also depends on how the server is configured — a slot count set against a
/// unified KV cache reprocesses the prompt anyway. Confirm it from the server's own
/// slot-selection log rather than from wall-clock time.
/// </remarks>
public static class ScoringRequestWriter
{
    private static readonly JsonWriterOptions Options = new()
    {
        // Indented because a person reads this: they paste it into a chat window,
        // and the first thing they do when a ranking looks wrong is scroll it.
        Indented = true
    };

    public static string Write(ScoringRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            WriteRequest(writer, request);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteRequest(Utf8JsonWriter writer, ScoringRequest request)
    {
        writer.WriteStartObject();

        // ---- Invariant across a sweep's batches. Everything above "history" is part
        // ---- of the prefix a server can reuse; see the note on this class.
        writer.WriteStartObject("aniqueue");
        writer.WriteString("format", ScoringRequest.RequestFormat);
        writer.WriteNumber("version", ScoringRequest.CurrentVersion);

        // Which library is being ranked, for the reply to echo back. Invariant
        // for the life of a database, so it belongs in the prefix and costs a sweep
        // nothing: it is written once per request and never varies between batches.
        //
        // No version bump goes with it. The field is additive in both directions — a
        // reply that omits it is read exactly as replies were before — and raising the
        // version would refuse every reply a user is currently holding, which is the
        // harm this whole change exists to prevent rather than to cause.
        if (!string.IsNullOrEmpty(request.Library))
        {
            writer.WriteString("library", request.Library);
        }

        writer.WriteEndObject();

        writer.WriteStartObject("scale");
        writer.WriteNumber("min", request.Scale.Min);
        writer.WriteNumber("max", request.Scale.Max);
        writer.WriteEndObject();

        // Stated even when nothing was capped, so a reader never has to infer whether
        // a short list means a small library or a truncated one. This one belongs
        // above the history it describes and is safe there: it counts rated titles,
        // which a sweep does not change.
        writer.WriteNumber("historyAvailable", request.HistoryAvailable);

        writer.WriteStartArray("history");
        foreach (var entry in request.History)
        {
            writer.WriteStartObject();
            writer.WriteString("title", entry.Title);
            writer.WriteNumber("score", entry.Score);
            WriteMediaType(writer, entry.MediaType);
            WriteOptionalNumber(writer, "year", entry.Year);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        // ---- Varies between batches. Nothing below here may move above "history".

        // Shrinks as a sweep scores its way through the backlog, which is exactly why
        // it sits below the history rather than beside historyAvailable where it
        // reads more naturally.
        writer.WriteNumber("candidatesAvailable", request.CandidatesAvailable);

        // Written only when it narrows something. A model reading "return the top
        // 182 of 182" has been given an instruction that is really a no-op, and a
        // no-op instruction is one more thing for a small model to misread.
        if (request.IsRankingLimited)
        {
            writer.WriteNumber("returnTop", request.ExpectedResults);
        }

        writer.WriteStartArray("candidates");
        foreach (var candidate in request.Candidates)
        {
            WriteCandidate(writer, candidate);
        }

        writer.WriteEndArray();

        // Last, because it changes on every single request and would otherwise be the
        // one field guaranteed to break the prefix. It is informational — a person
        // reading a pasted request wants to know when it was built — so the bottom of
        // the document costs it nothing.
        writer.WriteString("generatedAt", request.GeneratedAt);

        writer.WriteEndObject();
    }

    private static void WriteCandidate(Utf8JsonWriter writer, ScoringCandidate candidate)
    {
        writer.WriteStartObject();

        writer.WriteNumber("id", candidate.Id);
        writer.WriteString("title", candidate.Title);

        if (candidate.Titles.Any)
        {
            writer.WriteStartObject("titles");
            WriteOptionalString(writer, "romaji", candidate.Titles.Romaji);
            WriteOptionalString(writer, "english", candidate.Titles.English);
            WriteOptionalString(writer, "native", candidate.Titles.Native);
            writer.WriteEndObject();
        }

        WriteMediaType(writer, candidate.MediaType);
        // No "episodes" and no "episodeMinutes". They were about a tenth of a real
        // request and changed no score anyone could measure.
        WriteOptionalNumber(writer, "year", candidate.Year);

        if (candidate.ExternalIds.Any)
        {
            writer.WriteStartObject("externalIds");
            WriteOptionalString(writer, "anilist", candidate.ExternalIds.AniList);
            WriteOptionalString(writer, "myanimelist", candidate.ExternalIds.MyAnimeList);
            writer.WriteEndObject();
        }

        WriteOptionalString(writer, "notes", candidate.Notes);

        writer.WriteEndObject();
    }

    // Unknown is the default for a reason — imports routinely omit it — and writing
    // "unknown" would present an absence as a fact about the title.
    private static void WriteMediaType(Utf8JsonWriter writer, MediaType mediaType)
    {
        if (mediaType != MediaType.Unknown)
        {
            writer.WriteString("mediaType", mediaType.ToString());
        }
    }

    private static void WriteOptionalNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is not null)
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(name, value);
        }
    }
}
