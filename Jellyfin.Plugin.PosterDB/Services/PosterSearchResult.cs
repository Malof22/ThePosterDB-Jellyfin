namespace Jellyfin.Plugin.PosterDB.Services;

/// <summary>
/// One individual poster found on ThePosterDB.
/// </summary>
/// <param name="PosterId">Individual poster ID (from data-poster-id on the overlay div).</param>
/// <param name="ImageUrl">Direct URL to the full-size poster image (images.theposterdb.com).</param>
/// <param name="ThumbnailUrl">URL to show as thumbnail in the picker (same as ImageUrl).</param>
public sealed record PosterSearchResult(string PosterId, string? ImageUrl, string? ThumbnailUrl);
