using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PosterDB.Services;

/// <summary>
/// Abstracts all communication with ThePosterDB.
/// </summary>
public interface IPosterDbService
{
    /// <summary>
    /// Ensures the service has an active authenticated session.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if authenticated.</returns>
    Task<bool> EnsureAuthenticatedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resets the cached session so the next request triggers a fresh login.
    /// </summary>
    void InvalidateSession();

    /// <summary>
    /// Searches ThePosterDB for posters matching the given item.
    /// </summary>
    /// <param name="itemName">Display name of the movie or show (used as the search term).</param>
    /// <param name="tmdbId">TMDb numeric ID, or null.</param>
    /// <param name="imdbId">IMDb ID (tt…), or null.</param>
    /// <param name="tvdbId">TVDb numeric ID, or null.</param>
    /// <param name="category">ThePosterDB category: "Movies", "Shows", or "Collections".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of search results.</returns>
    Task<IReadOnlyList<PosterSearchResult>> SearchPostersAsync(
        string? itemName,
        string? tmdbId,
        string? imdbId,
        string? tvdbId,
        string category,
        CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the highest-quality version of a poster by its ID.
    /// Fetches the poster detail page to resolve the actual image URL first.
    /// </summary>
    /// <param name="posterId">Numeric poster ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>HTTP response whose content is the image.</returns>
    Task<HttpResponseMessage> GetPosterImageResponseAsync(string posterId, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads an image from an arbitrary URL using the authenticated session.
    /// </summary>
    /// <param name="url">Absolute image URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>HTTP response whose content is the image.</returns>
    Task<HttpResponseMessage> GetImageResponseAsync(string url, CancellationToken cancellationToken);
}
