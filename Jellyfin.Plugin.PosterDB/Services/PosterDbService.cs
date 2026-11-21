using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterDB.Services;

/// <summary>
/// Implements authenticated scraping of ThePosterDB.
/// </summary>
public sealed class PosterDbService : IPosterDbService, IDisposable
{
    private const string BaseUrl = "https://theposterdb.com";

    private static readonly Regex PosterIdRegex = new Regex(
        @"/posters/(?<id>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(500));

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PosterDbService> _logger;
    private readonly SemaphoreSlim _authLock = new SemaphoreSlim(1, 1);
    private readonly HtmlParser _htmlParser = new HtmlParser();
    private readonly ConcurrentDictionary<string, (DateTime ExpiresAt, IReadOnlyList<PosterSearchResult> Results)> _searchCache = new(StringComparer.Ordinal);

    private string? _cookieHeader;
    private bool _isAuthenticated;

    /// <summary>
    /// Initializes a new instance of the <see cref="PosterDbService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public PosterDbService(IHttpClientFactory httpClientFactory, ILogger<PosterDbService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (_isAuthenticated)
        {
            return true;
        }

        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isAuthenticated)
            {
                return true;
            }

            // Check env vars first so the Jellyfin-dependent method is never JIT-compiled
            // when running outside Jellyfin (e.g. in the test CLI)
            var email = Environment.GetEnvironmentVariable("POSTERDB_LOGIN");
            var password = Environment.GetEnvironmentVariable("POSTERDB_PASSWORD");

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                var (pluginEmail, pluginPassword) = GetPluginCredentials();
                email = string.IsNullOrWhiteSpace(email) ? pluginEmail : email;
                password = string.IsNullOrWhiteSpace(password) ? pluginPassword : password;
            }

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                _logger.LogWarning("PosterDB credentials not configured — open the plugin settings page");
                return false;
            }

            return await LoginAsync(email, password, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <inheritdoc />
    public void InvalidateSession()
    {
        _isAuthenticated = false;
        _cookieHeader = null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PosterSearchResult>> SearchPostersAsync(
        string? itemName,
        string? tmdbId,
        string? imdbId,
        string? tvdbId,
        string category,
        CancellationToken cancellationToken)
    {
        if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var query = BuildSearchQuery(itemName, tmdbId, imdbId, tvdbId);
        if (string.IsNullOrEmpty(query))
        {
            _logger.LogDebug("PosterDB search skipped — no name or ID for {Category}", category);
            return [];
        }

        var cacheKey = $"{query}|{category}";
        if (_searchCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.ExpiresAt)
        {
            _logger.LogDebug("PosterDB cache hit for {Query} ({Count} results)", query, cached.Results.Count);
            return cached.Results;
        }

        var html = await FetchHtmlAsync(query, cancellationToken).ConfigureAwait(false);
        if (html is null)
        {
            return [];
        }

        // Step 1: extract set IDs from the search page
        using var searchDoc = await _htmlParser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
        var setIds = ParseSearchResultSetIds(searchDoc);
        _logger.LogInformation("PosterDB search {Query} found {Count} set(s)", query, setIds.Count);

        if (setIds.Count == 0)
        {
            return [];
        }

        // Step 2: fetch each set page (with pagination) and extract individual poster cards
        using var throttle = new SemaphoreSlim(6, 6);
        var tasks = setIds.Select(async setId =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await FetchAllSetPostersAsync(setId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        });

        var allPosterLists = await Task.WhenAll(tasks).ConfigureAwait(false);
        var results = allPosterLists.SelectMany(x => x).ToList();
        _logger.LogInformation("PosterDB search returned {Count} individual poster(s)", results.Count);

        _searchCache[cacheKey] = (DateTime.UtcNow.Add(CacheTtl), results);
        return results;
    }

    /// <inheritdoc />
    public async Task<HttpResponseMessage> GetPosterImageResponseAsync(
        string imageUrl,
        CancellationToken cancellationToken)
    {
        // imageUrl is now the direct images.theposterdb.com URL (public, no auth needed)
        return await FetchImageAsync(imageUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HttpResponseMessage> GetImageResponseAsync(
        string url,
        CancellationToken cancellationToken)
    {
        if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        return await FetchImageAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _authLock.Dispose();
    }

    private static string BuildSearchQuery(
        string? itemName,
        string? tmdbId,
        string? imdbId,
        string? tvdbId)
    {
        // /search?term={name} is the only endpoint that returns results reliably.
        // The advanced search (/search/advanced/results?tmdb_id=…) returns inconsistent
        // results depending on server-side cache state and auth context.
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return string.Empty;
        }

        _ = tmdbId;
        _ = imdbId;
        _ = tvdbId;

        return $"/search?term={Uri.EscapeDataString(itemName)}";
    }

    private static IReadOnlyList<string> ParseSearchResultSetIds(IDocument document)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var setIds = new List<string>();

        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href") ?? string.Empty;
            var match = PosterIdRegex.Match(href);
            if (!match.Success)
            {
                continue;
            }

            var setId = match.Groups["id"].Value;
            if (seen.Add(setId))
            {
                setIds.Add(setId);
            }
        }

        return setIds;
    }

    private static int DetectMaxPage(IDocument document, string setId)
    {
        var maxPage = 1;
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href") ?? string.Empty;
            var idx = href.IndexOf($"/posters/{setId}?page=", StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }

            var numStart = idx + $"/posters/{setId}?page=".Length;
            var numEnd = numStart;
            while (numEnd < href.Length && char.IsDigit(href[numEnd]))
            {
                numEnd++;
            }

            if (numEnd > numStart && int.TryParse(href.AsSpan(numStart, numEnd - numStart), out var page))
            {
                maxPage = Math.Max(maxPage, page);
            }
        }

        return maxPage;
    }

    private static IReadOnlyList<PosterSearchResult> ParseSetPosters(IDocument document)
    {
        var results = new List<PosterSearchResult>();

        // Each poster card: div.hovereffect > picture + div.overlay[data-poster-id]
        foreach (var hoverDiv in document.QuerySelectorAll("div.hovereffect"))
        {
            var webpSrc = hoverDiv.QuerySelector("source[type='image/webp']")?.GetAttribute("srcset");
            if (string.IsNullOrEmpty(webpSrc) || webpSrc.Contains("/images/defaults/", StringComparison.Ordinal))
            {
                continue;
            }

            var posterId = hoverDiv.QuerySelector("div.overlay[data-poster-id]")?.GetAttribute("data-poster-id");
            if (string.IsNullOrEmpty(posterId))
            {
                continue;
            }

            results.Add(new PosterSearchResult(posterId, webpSrc, webpSrc));
        }

        return results;
    }

    private static string? CollectSetCookies(System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        var parts = cookies
            .Select(c => c.Split(';')[0].Trim())
            .Where(c => c.Length > 0)
            .ToList();

        return parts.Count > 0 ? string.Join("; ", parts) : null;
    }

    private static string? MergeCookies(string? existing, string? newCookies)
    {
        if (string.IsNullOrEmpty(existing))
        {
            return newCookies;
        }

        if (string.IsNullOrEmpty(newCookies))
        {
            return existing;
        }

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in existing.Split(';').Select(c => c.Trim()).Where(c => c.Length > 0))
        {
            var idx = part.IndexOf('=', StringComparison.Ordinal);
            if (idx > 0)
            {
                dict[part[..idx]] = part;
            }
        }

        foreach (var part in newCookies.Split(';').Select(c => c.Trim()).Where(c => c.Length > 0))
        {
            var idx = part.IndexOf('=', StringComparison.Ordinal);
            if (idx > 0)
            {
                dict[part[..idx]] = part;
            }
        }

        return string.Join("; ", dict.Values);
    }

    // Isolated in its own method so the JIT only loads MediaBrowser.Common
    // when this method is actually called (never happens in the test CLI).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (string? Email, string? Password) GetPluginCredentials()
    {
        var config = Plugin.Instance?.Configuration;
        return (config?.Email, config?.Password);
    }

    private async Task<IReadOnlyList<PosterSearchResult>> FetchAllSetPostersAsync(
        string setId,
        CancellationToken cancellationToken)
    {
        // Fetch page 1 to get the first batch and detect total page count
        var page1Html = await FetchHtmlAsync($"/posters/{setId}", cancellationToken).ConfigureAwait(false);
        if (page1Html is null)
        {
            return [];
        }

        using var page1Doc = await _htmlParser.ParseDocumentAsync(page1Html, cancellationToken).ConfigureAwait(false);
        var page1Posters = ParseSetPosters(page1Doc);
        var maxPage = DetectMaxPage(page1Doc, setId);

        if (maxPage <= 1)
        {
            return page1Posters;
        }

        // Fetch remaining pages in parallel
        var remainingTasks = Enumerable.Range(2, maxPage - 1).Select(async page =>
        {
            var html = await FetchHtmlAsync($"/posters/{setId}?page={page}", cancellationToken).ConfigureAwait(false);
            if (html is null)
            {
                return (IReadOnlyList<PosterSearchResult>)[];
            }

            using var doc = await _htmlParser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
            return ParseSetPosters(doc);
        });

        var remainingResults = await Task.WhenAll(remainingTasks).ConfigureAwait(false);

        var combined = new List<PosterSearchResult>(page1Posters);
        foreach (var page in remainingResults)
        {
            combined.AddRange(page);
        }

        return combined;
    }

    private async Task<bool> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Authenticating with ThePosterDB as {Email}", email);

        // Use a fresh HttpClient with automatic cookie management (mirrors curl -c/-b jar behaviour).
        // Manual cookie extraction was subtly unreliable; letting the runtime handle it matches
        // exactly what a real browser or curl does.
        var cookieJar = new System.Net.CookieContainer();
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = cookieJar,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        using var loginClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
        loginClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:124.0) Gecko/20100101 Firefox/124.0");
        loginClient.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        loginClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");

        // Step 1: GET /login — cookies stored automatically in jar
        string? csrfToken;
        {
            using var getResp = await loginClient
                .GetAsync("/login", HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug("PosterDB GET /login → HTTP {Status}", (int)getResp.StatusCode);

            var html = await getResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            csrfToken = await ExtractCsrfTokenAsync(html, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("PosterDB CSRF token extracted: {Found}", csrfToken is not null ? "yes" : "no");
            await LogLoginFormFieldsAsync(html, cancellationToken).ConfigureAwait(false);
        }

        if (csrfToken is null)
        {
            _logger.LogError("Could not extract CSRF token from PosterDB login page — check plugin connectivity");
            return false;
        }

        // Step 2: POST credentials — cookies sent automatically from jar; AllowAutoRedirect=false
        // so we can inspect the Location header of the 302 response
        var formData = new Dictionary<string, string>
        {
            ["_token"] = csrfToken,
            ["login"] = email,
            ["password"] = password,
            ["remember"] = "on",
        };

        using var postReq = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = new FormUrlEncodedContent(formData),
        };
        postReq.Headers.Add("Referer", BaseUrl + "/login");
        postReq.Headers.Add("Origin", BaseUrl);

        using var postResp = await loginClient
            .SendAsync(postReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var location = postResp.Headers.Location?.OriginalString ?? string.Empty;
        _logger.LogDebug(
            "PosterDB POST /login → HTTP {Status}, Location: {Location}",
            (int)postResp.StatusCode,
            string.IsNullOrEmpty(location) ? "(none)" : location);

        if (postResp.StatusCode is HttpStatusCode.Found
            or HttpStatusCode.MovedPermanently
            or HttpStatusCode.SeeOther)
        {
            if (!location.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                // Pull all cookies from the jar (GET + POST Set-Cookie headers combined)
                var allCookies = cookieJar.GetCookies(new Uri(BaseUrl));
                _cookieHeader = string.Join("; ", allCookies.Cast<System.Net.Cookie>()
                    .Select(c => $"{c.Name}={c.Value}"));
                _isAuthenticated = true;
                _logger.LogInformation("Successfully authenticated with ThePosterDB");
                return true;
            }
        }

        _logger.LogError(
            "Authentication failed for ThePosterDB (HTTP {Status}, Location: {Location}) — verify credentials in plugin settings",
            (int)postResp.StatusCode,
            string.IsNullOrEmpty(location) ? "(none)" : location);
        return false;
    }

    private async Task<string?> FetchHtmlAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        const int MaxRedirects = 10;
        var client = _httpClientFactory.CreateClient("PosterDB");
        var currentUrl = relativeUrl;
        var posterDbHost = new Uri("https://theposterdb.com").Host;

        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var req = AuthenticatedGet(currentUrl);
                using var resp = await client
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (resp.StatusCode is HttpStatusCode.Found
                    or HttpStatusCode.MovedPermanently
                    or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect
                    or HttpStatusCode.PermanentRedirect)
                {
                    var location = resp.Headers.Location;
                    if (location is null)
                    {
                        break;
                    }

                    // Resolve relative locations against the base address
                    var resolved = location.IsAbsoluteUri
                        ? location
                        : new Uri(new Uri("https://theposterdb.com"), location);

                    _logger.LogDebug(
                        "PosterDB redirect {From} → {To}",
                        currentUrl,
                        resolved.PathAndQuery);

                    // Only follow redirects that stay on the same host
                    if (!resolved.Host.Equals(posterDbHost, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("PosterDB redirect leaves host ({Host}), stopping", resolved.Host);
                        break;
                    }

                    currentUrl = resolved.PathAndQuery;
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "PosterDB request to {Url} returned {Status}",
                        currentUrl,
                        (int)resp.StatusCode);
                    return null;
                }

                _logger.LogDebug("PosterDB fetched {Url} → {Final} (200)", relativeUrl, currentUrl);
                return await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            _logger.LogWarning("PosterDB redirect limit reached for {Url}", relativeUrl);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error while fetching {Url} from PosterDB", relativeUrl);
            return null;
        }
    }

    private async Task<HttpResponseMessage> FetchImageAsync(string url, CancellationToken cancellationToken)
    {
        var absoluteUrl = url.StartsWith("/", StringComparison.Ordinal) ? BaseUrl + url : url;
        using var req = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
        if (!string.IsNullOrEmpty(_cookieHeader))
        {
            req.Headers.Add("Cookie", _cookieHeader);
        }

        var client = _httpClientFactory.CreateClient("PosterDB");

        // Response body is consumed by the caller — do not dispose here
        return await client
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task LogLoginFormFieldsAsync(string html, CancellationToken cancellationToken)
    {
        using var doc = await _htmlParser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
        var form = doc.QuerySelector("form[action*='login'], form[method='post']");
        if (form is null)
        {
            _logger.LogWarning("PosterDB: no POST form found on login page");
            return;
        }

        // Log all input fields (name + type, NOT value for security)
        var inputs = form.QuerySelectorAll("input");
        _logger.LogInformation(
            "PosterDB login form has {Count} input(s): {Fields}",
            inputs.Length,
            string.Join(", ", inputs.Select(i => $"{i.GetAttribute("name") ?? "?"}[{i.GetAttribute("type") ?? "text"}]")));

        // Specifically flag Cloudflare Turnstile
        var turnstile = doc.QuerySelector("[data-sitekey], .cf-turnstile, [name='cf-turnstile-response']");
        if (turnstile is not null)
        {
            _logger.LogError(
                "PosterDB: Cloudflare Turnstile detected on login page (sitekey={Key}) — automated login will fail",
                turnstile.GetAttribute("data-sitekey") ?? "?");
        }
        else
        {
            _logger.LogDebug("PosterDB: no Cloudflare Turnstile detected on login page");
        }
    }

    private async Task<string?> ExtractCsrfTokenAsync(string html, CancellationToken cancellationToken)
    {
        using var doc = await _htmlParser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);

        // Laravel standard: <meta name="csrf-token" content="…">
        var meta = doc.QuerySelector("meta[name='csrf-token']");
        if (meta?.GetAttribute("content") is { Length: > 0 } metaValue)
        {
            return metaValue;
        }

        // Alternative: hidden input <input type="hidden" name="_token" value="…">
        return doc.QuerySelector("input[name='_token']")?.GetAttribute("value");
    }

    private HttpRequestMessage AuthenticatedGet(string relativeUrl)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        if (!string.IsNullOrEmpty(_cookieHeader))
        {
            req.Headers.Add("Cookie", _cookieHeader);
        }

        return req;
    }
}
