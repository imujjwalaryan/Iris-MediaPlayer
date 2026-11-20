using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Iris_MediaPlayer.Services.Stash;

/// <summary>Something went wrong talking to Stash, with a message that can be shown as is.</summary>
public sealed class StashException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>How a scene list is narrowed down.</summary>
public enum StashSceneFilter { All, Unwatched, InProgress, Rated }

/// <summary>How a scene list is ordered (Stash's sort names).</summary>
public enum StashSceneSort { RecentlyAdded, Date, Title, Rating, MostPlayed, RecentlyPlayed, Duration, Random }

/// <summary>Scenes limited to one performer, studio or tag.</summary>
public sealed record StashScope(StashKind Kind, string Id, string Name);

/// <summary>A page of results and how many there are in all.</summary>
public sealed record StashPage(IReadOnlyList<StashItem> Items, int Total);

/// <summary>
/// A client for Stash's GraphQL API (/graphql) with its API key in the ApiKey header. Scenes,
/// performers, studios and tags are read with JsonDocument; requests are built with JsonObject, so
/// nothing here needs reflection.
/// </summary>
public sealed class StashClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private const string SceneFields = """
        id title details date rating100 o_counter play_count resume_time last_played_at created_at
        files { path duration width height video_codec audio_codec size frame_rate }
        paths { screenshot }
        studio { id name image_path }
        performers { id name image_path favorite }
        tags { id name }
        scene_markers { id title seconds primary_tag { name } }
        """;

    public StashClient(string address, string? apiKey)
    {
        Address = NormalizeAddress(address);
        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    public string Address { get; }
    public string? ApiKey { get; }

    /// <summary>"192.168.1.10:9999" → "http://192.168.1.10:9999"; drops a trailing slash.</summary>
    public static string NormalizeAddress(string address)
    {
        var text = address.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;
        return text.TrimEnd('/');
    }

    // ---------- Connection ----------

    /// <summary>Checks the address and key by counting scenes; returns how many there are.</summary>
    public async Task<int> CheckAsync(CancellationToken cancel = default)
    {
        using var json = await QueryAsync("query { findScenes(filter: { per_page: 1 }) { count } }", null, cancel, "connect to Stash");
        return json.RootElement.GetProperty("data").GetProperty("findScenes").GetProperty("count").GetInt32();
    }

    // ---------- Scenes ----------

    public async Task<StashPage> FindScenesAsync(StashSceneFilter filter, StashSceneSort sort, string? search, StashScope? scope,
        int page, int perPage, CancellationToken cancel = default)
    {
        var (sortName, descending) = sort switch
        {
            StashSceneSort.Date => ("date", true),
            StashSceneSort.Title => ("title", false),
            StashSceneSort.Rating => ("rating", true),
            StashSceneSort.MostPlayed => ("play_count", true),
            StashSceneSort.RecentlyPlayed => ("last_played_at", true),
            StashSceneSort.Duration => ("duration", true),
            StashSceneSort.Random => ("random_" + RandomSeed, false),
            _ => ("created_at", true)
        };

        var sceneFilter = new JsonObject();
        switch (filter)
        {
            case StashSceneFilter.Unwatched:
                sceneFilter["play_count"] = Criterion(0, "EQUALS");
                break;
            case StashSceneFilter.InProgress:
                sceneFilter["resume_time"] = Criterion(0, "GREATER_THAN");
                break;
            case StashSceneFilter.Rated:
                sceneFilter["rating100"] = Criterion(0, "GREATER_THAN");
                break;
        }
        if (scope is not null)
        {
            var field = scope.Kind switch { StashKind.Performer => "performers", StashKind.Studio => "studios", _ => "tags" };
            var ids = new JsonObject { ["value"] = new JsonArray(scope.Id), ["modifier"] = "INCLUDES" };
            if (scope.Kind != StashKind.Performer)
                ids["depth"] = 0;
            sceneFilter[field] = ids;
        }

        var variables = new JsonObject
        {
            ["filter"] = PageFilter(search, page, perPage, sortName, descending),
            ["scene_filter"] = sceneFilter
        };
        using var json = await QueryAsync(
            $"query($filter: FindFilterType, $scene_filter: SceneFilterType) {{ findScenes(filter: $filter, scene_filter: $scene_filter) {{ count scenes {{ {SceneFields} }} }} }}",
            variables, cancel, "load scenes");
        var result = json.RootElement.GetProperty("data").GetProperty("findScenes");
        return new StashPage(result.GetProperty("scenes").EnumerateArray().Select(ReadScene).ToList(), result.GetProperty("count").GetInt32());
    }

    public async Task<StashItem> FindSceneAsync(string id, CancellationToken cancel = default)
    {
        using var json = await QueryAsync($"query($id: ID) {{ findScene(id: $id) {{ {SceneFields} }} }}",
            new JsonObject { ["id"] = id }, cancel, "load this scene");
        var scene = json.RootElement.GetProperty("data").GetProperty("findScene");
        if (scene.ValueKind != JsonValueKind.Object)
            throw new StashException("Stash doesn't have this scene any more.");
        return ReadScene(scene);
    }

    // ---------- Performers, studios, tags ----------

    public async Task<StashPage> FindAsync(StashKind kind, string? search, int page, int perPage, bool popularFirst,
        CancellationToken cancel = default)
    {
        var (query, result, fields) = kind switch
        {
            StashKind.Performer => ("findPerformers", "performers", "id name image_path scene_count favorite"),
            StashKind.Studio => ("findStudios", "studios", "id name image_path scene_count favorite"),
            _ => ("findTags", "tags", "id name image_path scene_count favorite")
        };
        var variables = new JsonObject
        {
            ["filter"] = PageFilter(search, page, perPage, popularFirst ? "scenes_count" : "name", popularFirst)
        };
        JsonDocument json;
        try
        {
            json = await QueryAsync($"query($filter: FindFilterType) {{ {query}(filter: $filter) {{ count {result} {{ {fields} }} }} }}",
                variables, cancel, "load " + result);
        }
        catch (StashException) when (popularFirst)
        {
            // Older Stash versions name the sort differently; fall back to names.
            return await FindAsync(kind, search, page, perPage, popularFirst: false, cancel);
        }
        using (json)
        {
            var data = json.RootElement.GetProperty("data").GetProperty(query);
            var items = data.GetProperty(result).EnumerateArray().Select(element => ReadNamed(element, kind)).ToList();
            return new StashPage(items, data.GetProperty("count").GetInt32());
        }
    }

    // ---------- Playback activity ----------

    /// <summary>Saves where playback is and adds the time watched since the last save.</summary>
    public async Task SaveActivityAsync(string sceneId, double resumeSeconds, double watchedSeconds)
    {
        using var _ = await QueryAsync("mutation($id: ID!, $resume: Float, $played: Float) { sceneSaveActivity(id: $id, resume_time: $resume, playDuration: $played) }",
            new JsonObject { ["id"] = sceneId, ["resume"] = Math.Max(0, resumeSeconds), ["played"] = Math.Max(0, watchedSeconds) },
            CancellationToken.None, "save progress");
    }

    /// <summary>Adds one play to the scene's play count (now).</summary>
    public async Task AddPlayAsync(string sceneId)
    {
        using var _ = await QueryAsync("mutation($id: ID!) { sceneAddPlay(id: $id) { count } }",
            new JsonObject { ["id"] = sceneId }, CancellationToken.None, "count the play");
    }

    // ---------- Links ----------

    /// <summary>The original file, streamed with range requests; the key goes in the link for mpv.</summary>
    public string GetStreamUrl(string sceneId) => WithKey($"{Address}/scene/{sceneId}/stream");

    /// <summary>A picture link from Stash with the API key added (Stash leaves it out of picture links).</summary>
    public string? GetImageUrl(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        // Stash marks "no picture" with default=true.
        if (path.Contains("default=true", StringComparison.OrdinalIgnoreCase))
            return null;
        var absolute = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : Address + "/" + path.TrimStart('/');
        return WithKey(absolute);
    }

    private string WithKey(string url)
        => ApiKey is null ? url : url + (url.Contains('?') ? "&" : "?") + "apikey=" + Uri.EscapeDataString(ApiKey);

    // ---------- Reading answers ----------

    private static readonly int RandomSeed = Random.Shared.Next(10_000_000, 99_999_999);

    private static JsonObject PageFilter(string? search, int page, int perPage, string sort, bool descending)
    {
        var filter = new JsonObject
        {
            ["page"] = Math.Max(1, page),
            ["per_page"] = perPage,
            ["sort"] = sort,
            ["direction"] = descending ? "DESC" : "ASC"
        };
        if (!string.IsNullOrWhiteSpace(search))
            filter["q"] = search.Trim();
        return filter;
    }

    private static JsonObject Criterion(int value, string modifier) => new() { ["value"] = value, ["modifier"] = modifier };

    private static StashItem ReadScene(JsonElement element)
    {
        JsonElement? file = element.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0
            ? files[0] : null;
        var title = GetString(element, "title");
        return new StashItem
        {
            Id = GetString(element, "id") ?? string.Empty,
            Kind = StashKind.Scene,
            Name = string.IsNullOrWhiteSpace(title) ? StashItem.NameFromPath(file is { } f ? GetString(f, "path") : null) : title,
            Details = GetString(element, "details"),
            Date = GetString(element, "date"),
            Rating100 = GetInt(element, "rating100"),
            PlayCount = GetInt(element, "play_count") ?? 0,
            OCount = GetInt(element, "o_counter") ?? 0,
            ResumeSeconds = GetDouble(element, "resume_time") ?? 0,
            CreatedAt = GetDate(element, "created_at"),
            LastPlayedAt = GetDate(element, "last_played_at"),
            DurationSeconds = file is { } a ? GetDouble(a, "duration") ?? 0 : 0,
            Width = file is { } b ? GetInt(b, "width") ?? 0 : 0,
            Height = file is { } c ? GetInt(c, "height") ?? 0 : 0,
            VideoCodec = file is { } d ? GetString(d, "video_codec") : null,
            AudioCodec = file is { } e ? GetString(e, "audio_codec") : null,
            Size = file is { } g && g.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
            FrameRate = file is { } h ? GetDouble(h, "frame_rate") ?? 0 : 0,
            FilePath = file is { } i ? GetString(i, "path") : null,
            ScreenshotPath = element.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object ? GetString(paths, "screenshot") : null,
            Studio = element.TryGetProperty("studio", out var studio) && studio.ValueKind == JsonValueKind.Object ? ReadNamed(studio, StashKind.Studio) : null,
            Performers = ReadList(element, "performers", StashKind.Performer),
            Tags = ReadList(element, "tags", StashKind.Tag),
            Markers = element.TryGetProperty("scene_markers", out var markers) && markers.ValueKind == JsonValueKind.Array
                ? markers.EnumerateArray()
                    .Select(marker => new StashMarker(
                        GetString(marker, "id") ?? string.Empty,
                        GetString(marker, "title") is { Length: > 0 } markerTitle ? markerTitle
                            : marker.TryGetProperty("primary_tag", out var tag) && tag.ValueKind == JsonValueKind.Object ? GetString(tag, "name") ?? "Marker" : "Marker",
                        GetDouble(marker, "seconds") ?? 0))
                    .OrderBy(marker => marker.Seconds)
                    .ToList()
                : []
        };
    }

    private static IReadOnlyList<StashItem> ReadList(JsonElement element, string name, StashKind kind)
        => element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(entry => ReadNamed(entry, kind)).ToList()
            : [];

    private static StashItem ReadNamed(JsonElement element, StashKind kind) => new()
    {
        Id = GetString(element, "id") ?? string.Empty,
        Kind = kind,
        Name = GetString(element, "name") ?? "Unnamed",
        ImagePath = GetString(element, "image_path"),
        SceneCount = GetInt(element, "scene_count") ?? 0,
        IsFavorite = element.TryGetProperty("favorite", out var favorite) && favorite.ValueKind == JsonValueKind.True
    };

    // ---------- HTTP ----------

    private async Task<JsonDocument> QueryAsync(string query, JsonObject? variables, CancellationToken cancel, string action)
    {
        var body = new JsonObject { ["query"] = query };
        if (variables is not null)
            body["variables"] = variables;

        using var request = new HttpRequestMessage(HttpMethod.Post, Address + "/graphql")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (ApiKey is not null)
            request.Headers.TryAddWithoutValidation("ApiKey", ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancel);
        }
        catch (TaskCanceledException exception) when (!cancel.IsCancellationRequested)
        {
            throw new StashException($"Couldn't {action}: Stash took too long to answer.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new StashException($"Couldn't {action}: {exception.Message}", exception);
        }

        using (response)
        {
            if ((int)response.StatusCode is 401 or 403)
                throw new StashException(ApiKey is null
                    ? "Stash asked for a sign-in. Add the API key from Stash › Settings › Security."
                    : "Stash didn't accept the API key.");
            if (!response.IsSuccessStatusCode)
                throw new StashException($"Couldn't {action} (Stash answered {(int)response.StatusCode}).");

            var text = await response.Content.ReadAsStringAsync(cancel);
            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(text);
            }
            catch (JsonException exception)
            {
                throw new StashException($"Couldn't {action}: the answer wasn't from Stash.", exception);
            }

            if (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var message = GetString(errors[0], "message") ?? "unknown error";
                json.Dispose();
                throw new StashException($"Couldn't {action}: {message}");
            }
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                json.Dispose();
                throw new StashException($"Couldn't {action}: Stash sent an empty answer.");
            }
            return json;
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static double? GetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private static DateTime? GetDate(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
           && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;
}
