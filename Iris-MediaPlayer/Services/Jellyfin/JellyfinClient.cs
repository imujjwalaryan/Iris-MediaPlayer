using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Services.Jellyfin;

/// <summary>What a server says about itself before signing in.</summary>
public sealed record JellyfinServerInfo(string Address, string Id, string Name, string Version);

/// <summary>A signed-in user on a server.</summary>
public sealed record JellyfinSignIn(string ServerId, string UserId, string UserName, string AccessToken);

/// <summary>A Quick Connect request: show <see cref="Code"/> to the person, keep <see cref="Secret"/>.</summary>
public sealed record QuickConnectRequest(string Code, string Secret);

/// <summary>Something went wrong talking to the server, with a message that can be shown as is.</summary>
public sealed class JellyfinException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A small client for the parts of the Jellyfin HTTP API Iris uses: checking a server, signing in
/// (password or Quick Connect), listing libraries and items, and building stream and image links.
/// Responses are read with JsonDocument, so nothing here needs reflection.
/// </summary>
public sealed class JellyfinClient : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public JellyfinClient(string address, string? accessToken = null, string? userId = null)
    {
        Address = NormalizeAddress(address);
        AccessToken = accessToken;
        UserId = userId;
    }

    public string Address { get; }
    public string? AccessToken { get; private set; }
    public string? UserId { get; private set; }

    /// <summary>"jelly.example.com" → "https://jelly.example.com"; drops a trailing slash or "/web".</summary>
    public static string NormalizeAddress(string address)
    {
        var text = address.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;
        text = text.TrimEnd('/');
        foreach (var suffix in new[] { "/web/index.html", "/web" })
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                text = text[..^suffix.Length];
        }
        return text.TrimEnd('/');
    }

    // ---------- Server and sign-in ----------

    /// <summary>Checks that <see cref="Address"/> is a Jellyfin server and returns its name.</summary>
    public async Task<JellyfinServerInfo> GetServerInfoAsync(CancellationToken cancel = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "/System/Info/Public", null, cancel, "find a Jellyfin server at this address");
        var root = json.RootElement;
        var id = GetString(root, "Id");
        if (string.IsNullOrEmpty(id))
            throw new JellyfinException("That address answered, but it doesn't look like a Jellyfin server.");
        return new JellyfinServerInfo(Address, id, GetString(root, "ServerName") ?? "Jellyfin", GetString(root, "Version") ?? string.Empty);
    }

    public async Task<JellyfinSignIn> SignInAsync(string userName, string password, CancellationToken cancel = default)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["Username"] = userName, ["Pw"] = password },
            IrisJsonContext.Default.DictionaryStringString);
        using var json = await SendAsync(HttpMethod.Post, "/Users/AuthenticateByName", body, cancel, "sign in");
        return ReadSignIn(json.RootElement);
    }

    public async Task<bool> IsQuickConnectEnabledAsync(CancellationToken cancel = default)
    {
        try
        {
            using var json = await SendAsync(HttpMethod.Get, "/QuickConnect/Enabled", null, cancel, "check Quick Connect");
            return json.RootElement.ValueKind == JsonValueKind.True;
        }
        catch (JellyfinException)
        {
            return false;
        }
    }

    public async Task<QuickConnectRequest> StartQuickConnectAsync(CancellationToken cancel = default)
    {
        using var json = await SendAsync(HttpMethod.Post, "/QuickConnect/Initiate", null, cancel, "start Quick Connect");
        var code = GetString(json.RootElement, "Code");
        var secret = GetString(json.RootElement, "Secret");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(secret))
            throw new JellyfinException("The server didn't give a Quick Connect code.");
        return new QuickConnectRequest(code, secret);
    }

    /// <summary>True once the code has been approved on another signed-in device.</summary>
    public async Task<bool> IsQuickConnectApprovedAsync(QuickConnectRequest request, CancellationToken cancel = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "/QuickConnect/Connect?secret=" + Uri.EscapeDataString(request.Secret),
            null, cancel, "check Quick Connect");
        return json.RootElement.TryGetProperty("Authenticated", out var value) && value.ValueKind == JsonValueKind.True;
    }

    public async Task<JellyfinSignIn> FinishQuickConnectAsync(QuickConnectRequest request, CancellationToken cancel = default)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["Secret"] = request.Secret },
            IrisJsonContext.Default.DictionaryStringString);
        using var json = await SendAsync(HttpMethod.Post, "/Users/AuthenticateWithQuickConnect", body, cancel, "sign in");
        return ReadSignIn(json.RootElement);
    }

    /// <summary>Tells the server to forget this sign-in. Failures are ignored; the token is dropped locally anyway.</summary>
    public async Task SignOutAsync()
    {
        try
        {
            using var _ = await SendAsync(HttpMethod.Post, "/Sessions/Logout", null, CancellationToken.None, "sign out");
        }
        catch (JellyfinException)
        {
        }
    }

    private JellyfinSignIn ReadSignIn(JsonElement root)
    {
        var token = GetString(root, "AccessToken");
        if (string.IsNullOrEmpty(token) || !root.TryGetProperty("User", out var user))
            throw new JellyfinException("The server didn't accept the sign-in.");
        var userId = GetString(user, "Id") ?? throw new JellyfinException("The server didn't say who signed in.");
        AccessToken = token;
        UserId = userId;
        return new JellyfinSignIn(GetString(root, "ServerId") ?? string.Empty, userId, GetString(user, "Name") ?? string.Empty, token);
    }

    // ---------- Browsing ----------

    /// <summary>The user's video libraries (music, books and photos are left out).</summary>
    public async Task<IReadOnlyList<JellyfinItem>> GetLibrariesAsync(CancellationToken cancel = default)
    {
        var userId = RequireUser();
        JsonDocument json;
        try
        {
            json = await SendAsync(HttpMethod.Get, "/UserViews?userId=" + userId, null, cancel, "load your libraries");
        }
        catch (JellyfinException)
        {
            // Servers before 10.9 only have the older route.
            json = await SendAsync(HttpMethod.Get, $"/Users/{userId}/Views", null, cancel, "load your libraries");
        }

        using (json)
        {
            return ReadItems(json.RootElement)
                .Where(item => item.CollectionType is null or "movies" or "tvshows" or "homevideos" or "musicvideos" or "boxsets" or "mixed")
                .ToList();
        }
    }

    /// <summary>
    /// What's inside <paramref name="parent"/>. Movie and show libraries list every film or series in
    /// them, whatever folders they sit in; everything else lists its direct children.
    /// </summary>
    public async Task<IReadOnlyList<JellyfinItem>> GetChildrenAsync(JellyfinItem parent, CancellationToken cancel = default)
    {
        var userId = RequireUser();
        var query = new StringBuilder($"/Items?userId={userId}&parentId={parent.Id}")
            .Append("&fields=ProductionYear,RunTimeTicks,Container,DateCreated,UserData,SeriesName,ParentIndexNumber,IndexNumber")
            .Append("&enableImageTypes=Primary,Thumb,Backdrop&imageTypeLimit=1&enableTotalRecordCount=false");

        switch (parent.CollectionType)
        {
            case "movies":
                query.Append("&recursive=true&includeItemTypes=Movie&sortBy=SortName&sortOrder=Ascending");
                break;
            case "tvshows":
                query.Append("&recursive=true&includeItemTypes=Series&sortBy=SortName&sortOrder=Ascending");
                break;
            default:
                // Seasons and episodes in their order; everything else by name.
                query.Append(parent.Type is "Series" or "Season"
                    ? "&sortBy=ParentIndexNumber,IndexNumber,SortName&sortOrder=Ascending"
                    : "&sortBy=IsFolder,SortName&sortOrder=Descending,Ascending");
                break;
        }

        using var json = await SendAsync(HttpMethod.Get, query.ToString(), null, cancel, "load this library");
        return ReadItems(json.RootElement)
            .Where(item => item.IsFolder || item.IsPlayable)
            .ToList();
    }

    private string RequireUser() => UserId ?? throw new JellyfinException("Sign in to your Jellyfin server first.");

    private IEnumerable<JellyfinItem> ReadItems(JsonElement root)
    {
        if (!root.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var element in items.EnumerateArray())
        {
            var id = GetString(element, "Id");
            if (string.IsNullOrEmpty(id))
                continue;

            string? primaryTag = null;
            if (element.TryGetProperty("ImageTags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                primaryTag = GetString(tags, "Primary");

            double resumeSeconds = 0, playedPercent = 0;
            if (element.TryGetProperty("UserData", out var userData) && userData.ValueKind == JsonValueKind.Object)
            {
                if (userData.TryGetProperty("PlaybackPositionTicks", out var ticks) && ticks.TryGetInt64(out var position))
                    resumeSeconds = position / 10_000_000d;
                if (userData.TryGetProperty("PlayedPercentage", out var percent) && percent.TryGetDouble(out var value))
                    playedPercent = value;
            }

            yield return new JellyfinItem
            {
                Id = id,
                Name = GetString(element, "Name") ?? "Untitled",
                Type = GetString(element, "Type") ?? string.Empty,
                CollectionType = GetString(element, "CollectionType"),
                IsFolder = element.TryGetProperty("IsFolder", out var folder) && folder.ValueKind == JsonValueKind.True,
                Year = GetInt(element, "ProductionYear"),
                SeriesName = GetString(element, "SeriesName"),
                SeasonNumber = GetInt(element, "ParentIndexNumber"),
                EpisodeNumber = GetInt(element, "IndexNumber"),
                RunTimeSeconds = element.TryGetProperty("RunTimeTicks", out var runTime) && runTime.TryGetInt64(out var runTicks)
                    ? runTicks / 10_000_000d : 0,
                Container = GetString(element, "Container"),
                DateCreated = element.TryGetProperty("DateCreated", out var created) && created.TryGetDateTime(out var date)
                    ? date : DateTime.MinValue,
                ResumeSeconds = resumeSeconds,
                PlayedPercent = playedPercent,
                PrimaryImageTag = primaryTag
            };
        }
    }

    // ---------- Links ----------

    /// <summary>The item's main picture (poster, or a still for episodes), sized for a card.</summary>
    public string? GetImageUrl(JellyfinItem item, int height)
        => item.PrimaryImageTag is null
            ? null
            : $"{Address}/Items/{item.Id}/Images/Primary?fillHeight={height}&quality=90&tag={Uri.EscapeDataString(item.PrimaryImageTag)}";

    /// <summary>
    /// The original file, streamed as is ("direct play"). mpv seeks in it with HTTP range requests.
    /// The token is in the link because mpv can't send Jellyfin's sign-in header.
    /// </summary>
    public string GetStreamUrl(JellyfinItem item)
        => $"{Address}/Videos/{item.Id}/stream?static=true&mediaSourceId={item.Id}&api_key={Uri.EscapeDataString(AccessToken ?? string.Empty)}";

    // ---------- HTTP ----------

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? jsonBody, CancellationToken cancel, string action)
    {
        using var request = new HttpRequestMessage(method, Address + path);
        request.Headers.TryAddWithoutValidation("Authorization", BuildAuthorizationHeader());
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (jsonBody is not null || method == HttpMethod.Post)
            request.Content = new StringContent(jsonBody ?? string.Empty, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancel);
        }
        catch (TaskCanceledException exception) when (!cancel.IsCancellationRequested)
        {
            throw new JellyfinException($"Couldn't {action}: the server took too long to answer.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new JellyfinException($"Couldn't {action}: {exception.Message}", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new JellyfinException((int)response.StatusCode switch
                {
                    401 => action == "sign in" ? "Wrong username or password." : "The server signed you out. Sign in again.",
                    403 => $"Couldn't {action}: the server refused.",
                    404 => $"Couldn't {action}: this server doesn't support it.",
                    _ => $"Couldn't {action} (the server answered {(int)response.StatusCode})."
                });
            }

            var text = await response.Content.ReadAsStringAsync(cancel);
            try
            {
                return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            }
            catch (JsonException exception)
            {
                throw new JellyfinException($"Couldn't {action}: the answer wasn't from Jellyfin.", exception);
            }
        }
    }

    private string BuildAuthorizationHeader()
    {
        var header = $"MediaBrowser Client=\"Iris\", Device=\"{Escape(Environment.MachineName)}\", DeviceId=\"{JellyfinSession.DeviceId}\", Version=\"{AppVersion}\"";
        return AccessToken is null ? header : header + $", Token=\"{AccessToken}\"";

        static string Escape(string value) => value.Replace("\"", string.Empty, StringComparison.Ordinal);
    }

    private static readonly string AppVersion =
        typeof(JellyfinClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    public void Dispose()
    {
    }
}

/// <summary>A library, folder, show, season or video on a Jellyfin server.</summary>
public sealed class JellyfinItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Jellyfin's type: Movie, Series, Season, Episode, Video, Folder, BoxSet, CollectionFolder…</summary>
    public required string Type { get; init; }
    /// <summary>For libraries: movies, tvshows, homevideos… Null otherwise.</summary>
    public string? CollectionType { get; init; }
    public bool IsFolder { get; init; }
    public int? Year { get; init; }
    public string? SeriesName { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public double RunTimeSeconds { get; init; }
    public string? Container { get; init; }
    public DateTime DateCreated { get; init; }
    public double ResumeSeconds { get; init; }
    public double PlayedPercent { get; init; }
    public string? PrimaryImageTag { get; init; }

    public bool IsPlayable => !IsFolder && Type is "Movie" or "Episode" or "Video" or "MusicVideo" or "Trailer";

    /// <summary>Episodes and home videos have wide stills; everything else has tall posters.</summary>
    public bool IsWide => Type is "Episode" or "Video" or "MusicVideo" or "CollectionFolder" or "UserView";

    /// <summary>The name shown on cards: "3. Pilot" for episodes, the name otherwise.</summary>
    public string DisplayName => Type == "Episode" && EpisodeNumber is { } number ? $"{number}. {Name}" : Name;

    /// <summary>The small line under the name: year, running time or episode number.</summary>
    public string Caption
    {
        get
        {
            var parts = new List<string>();
            if (Type == "Episode" && SeasonNumber is { } season && EpisodeNumber is { } episode)
                parts.Add($"S{season}:E{episode}");
            else if (Year is { } year)
                parts.Add(year.ToString(CultureInfo.InvariantCulture));
            if (RunTimeSeconds > 0)
            {
                var time = TimeSpan.FromSeconds(RunTimeSeconds);
                parts.Add(time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} min" : $"{Math.Max(1, (int)time.TotalMinutes)} min");
            }
            if (parts.Count == 0)
                parts.Add(Type switch
                {
                    "Series" => "Show",
                    "Season" => "Season",
                    "BoxSet" => "Collection",
                    "Folder" or "CollectionFolder" => "Folder",
                    _ => string.Empty
                });
            return string.Join("  ·  ", parts.Where(part => part.Length > 0));
        }
    }

    /// <summary>How much has been watched, from 0 to 1 (for the progress line on cards).</summary>
    public double Progress => RunTimeSeconds > 0 && ResumeSeconds > 0
        ? Math.Clamp(ResumeSeconds / RunTimeSeconds, 0, 1)
        : 0;

    public Microsoft.UI.Xaml.Visibility ProgressVisibility
        => Progress > 0.01 && Progress < 0.97 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>Shown while the picture loads, or when there is none.</summary>
    public string Glyph => IsPlayable ? "\uE714" : Type == "Series" || Type == "Season" ? "\uE7F4" : "\uE8B7";

    /// <summary>The card picture, set by the page once it has the link.</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? Image { get; set; }
}
