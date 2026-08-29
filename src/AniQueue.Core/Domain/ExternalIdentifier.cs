namespace AniQueue.Core.Domain;

/// <summary>
/// An identifier a source claims for a title, before anything has been matched or
/// stored — the parse-time counterpart of <see cref="AnimeExternalId"/>.
/// </summary>
/// <remarks>
/// A parser emits one of these per identifier it knows: the MyAnimeList export
/// emits one, an AniList response emits its own id and <c>idMal</c>.
/// </remarks>
public sealed record ExternalIdentifier(AnimeSource Source, string Value);
