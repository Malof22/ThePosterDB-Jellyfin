using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PosterDB.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PosterDB.Api;

/// <summary>
/// API endpoints for the PosterDB plugin.
/// </summary>
[ApiController]
[Route("Plugins/PosterDB")]
[Produces(MediaTypeNames.Application.Json)]
[Authorize(Roles = "Administrator")]
public sealed class PosterDbController : ControllerBase
{
    private readonly IPosterDbService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="PosterDbController"/> class.
    /// </summary>
    /// <param name="service">PosterDB service.</param>
    public PosterDbController(IPosterDbService service)
    {
        _service = service;
    }

    /// <summary>
    /// Tests the ThePosterDB credentials saved in the plugin configuration.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 on success, 401 when credentials are wrong or missing.</returns>
    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> TestConnection(CancellationToken cancellationToken)
    {
        // Force a fresh login attempt
        _service.InvalidateSession();

        var ok = await _service.EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            return Unauthorized();
        }

        return Ok(new { status = "Connected" });
    }
}
