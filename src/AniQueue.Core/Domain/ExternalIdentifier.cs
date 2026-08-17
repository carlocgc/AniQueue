namespace AniQueue.Core.Domain;

/// <summary>
/// An identifier a source claims for a title, before anything has been matched or
/// stored — the parse-time counterpart of <see cref="AnimeExternalId"/>.
/// </summary>
/// <remarks>
/// This exists so a parser can emit more than one identifier per entry. The
/// MyAnimeList export knows only itself and emits one; an AniList response carries
/// its own id and <c>idMal</c> and emits two. Nothing downstream needs to know
/// which parser produced which, and that is what makes D17's bridge work in both
/// directions from a single matching path.
/// </remarks>
public sealed record ExternalIdentifier(AnimeSource Source, string Value);
