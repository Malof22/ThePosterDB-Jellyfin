using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PosterDB.Configuration;

/// <summary>
/// Plugin configuration persisted to XML.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the ThePosterDB account email address.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ThePosterDB account password.
    /// </summary>
    public string Password { get; set; } = string.Empty;
}
