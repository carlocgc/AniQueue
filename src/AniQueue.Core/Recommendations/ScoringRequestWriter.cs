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

        writer.WriteStartObject("aniqueue");
        writer.WriteString("format", ScoringRequest.RequestFormat);
        writer.WriteNumber("version", ScoringRequest.CurrentVersion);
        writer.WriteString("generatedAt", request.GeneratedAt);
        writer.WriteEndObject();

        writer.WriteStartObject("scale");
        writer.WriteNumber("min", request.Scale.Min);
        writer.WriteNumber("max", request.Scale.Max);
        writer.WriteEndObject();

        // Stated even when nothing was capped, so a reader never has to infer
        // whether a short history means a small library or a truncated one.
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

        writer.WriteStartArray("candidates");
        foreach (var candidate in request.Candidates)
        {
            WriteCandidate(writer, candidate);
        }

        writer.WriteEndArray();

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
        WriteOptionalNumber(writer, "episodes", candidate.Episodes);
        WriteOptionalNumber(writer, "episodeMinutes", candidate.EpisodeMinutes);
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
