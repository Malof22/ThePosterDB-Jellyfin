using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PosterDB.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterDB.Providers;

/// <summary>
/// Exposes ThePosterDB as a remote image source inside Jellyfin.
/// </summary>
public sealed class PosterDbImageProvider : IRemoteImageProvider
{
    // URI scheme used as a stable key passed from GetImages to GetImageResponse.
    // Format: posterdb://{posterId}
    private const string PosterDbScheme = "posterdb://";

    private readonly IPosterDbService _service;
    private readonly ILogger<PosterDbImageProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PosterDbImageProvider"/> class.
    /// </summary>
    /// <param name="service">PosterDB service.</param>
    /// <param name="logger">Logger.</param>
    public PosterDbImageProvider(IPosterDbService service, ILogger<PosterDbImageProvider> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => Plugin.ProviderName;

    /// <inheritdoc />
    public bool Supports(BaseItem item) =>
        item is Movie or Series or BoxSet;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        yield return ImageType.Primary;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        var category = item switch
        {
            Movie => "Movies",
            BoxSet => "Collections",
            _ => "Shows",
        };

        var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
        var imdbId = item.GetProviderId(MetadataProvider.Imdb);
        var tvdbId = item.GetProviderId(MetadataProvider.Tvdb);

        _logger.LogDebug(
            "PosterDB image search for {Name} (tmdb={TmdbId} imdb={ImdbId} tvdb={TvdbId})",
            item.Name,
            tmdbId,
            imdbId,
            tvdbId);

        var results = await _service.SearchPostersAsync(
            item.Name,
            tmdbId,
            imdbId,
            tvdbId,
            category,
            cancellationToken).ConfigureAwait(false);

        var images = new List<RemoteImageInfo>(results.Count);
        foreach (var r in results)
        {
            var imageUrl = r.ImageUrl ?? (PosterDbScheme + r.PosterId);
            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = imageUrl,
                ThumbnailUrl = r.ThumbnailUrl ?? imageUrl,
            });
        }

        return images;
    }

    /// <inheritdoc />
    public async Task<HttpResponseMessage> GetImageResponse(
        string url,
        CancellationToken cancellationToken)
    {
        if (url.StartsWith(PosterDbScheme, System.StringComparison.OrdinalIgnoreCase))
        {
            var posterId = url[PosterDbScheme.Length..];
            return await _service.GetPosterImageResponseAsync(posterId, cancellationToken)
                .ConfigureAwait(false);
        }

        // Fall back for plain HTTPS image URLs (future-proofing)
        return await _service.GetImageResponseAsync(url, cancellationToken).ConfigureAwait(false);
    }
}
