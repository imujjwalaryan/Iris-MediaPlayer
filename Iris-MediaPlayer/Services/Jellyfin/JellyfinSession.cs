using System;

namespace Iris_MediaPlayer.Services.Jellyfin;

/// <summary>
/// The Jellyfin server Iris is signed in to, kept between runs. The address, server and user names
/// live in Iris's settings; the access token lives in Windows Credential Manager (under
/// "Iris/Jellyfin/…"), never in Iris's own files. Use on the UI thread.
/// </summary>
internal static class JellyfinSession
{
    private const string AddressKey = "JellyfinAddress";
    private const string ServerIdKey = "JellyfinServerId";
    private const string ServerNameKey = "JellyfinServerName";
    private const string UserIdKey = "JellyfinUserId";
    private const string UserNameKey = "JellyfinUserName";
    private const string DeviceIdKey = "JellyfinDeviceId";
    private const string CredentialPrefix = "Iris/Jellyfin/";

    /// <summary>Raised after signing in or out.</summary>
    public static event Action? Changed;

    /// <summary>Identifies this PC to the server, so its sign-in shows up as one device.</summary>
    public static string DeviceId
    {
        get
        {
            var id = AppStorage.GetString(DeviceIdKey);
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                AppStorage.SetString(DeviceIdKey, id);
            }
            return id;
        }
    }

    public static string? Address => AppStorage.GetString(AddressKey);
    public static string? ServerName => AppStorage.GetString(ServerNameKey);
    public static string? UserName => AppStorage.GetString(UserNameKey);
    public static string? ServerId => AppStorage.GetString(ServerIdKey);

    public static bool IsSignedIn => CreateClient() is not null;

    /// <summary>A client for the saved sign-in, or null when not signed in (or the token is gone).</summary>
    public static JellyfinClient? CreateClient()
    {
        var address = Address;
        var userId = AppStorage.GetString(UserIdKey);
        var serverId = ServerId;
        if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(serverId))
            return null;
        var token = CredentialStore.Read(CredentialPrefix + serverId);
        return string.IsNullOrEmpty(token) ? null : new JellyfinClient(address, token, userId);
    }

    public static void Save(JellyfinServerInfo server, JellyfinSignIn signIn)
    {
        var serverId = string.IsNullOrEmpty(signIn.ServerId) ? server.Id : signIn.ServerId;
        try
        {
            CredentialStore.Write(CredentialPrefix + serverId, signIn.UserName, signIn.AccessToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new JellyfinException(exception.Message, exception);
        }
        AppStorage.SetString(AddressKey, server.Address);
        AppStorage.SetString(ServerIdKey, serverId);
        AppStorage.SetString(ServerNameKey, server.Name);
        AppStorage.SetString(UserIdKey, signIn.UserId);
        AppStorage.SetString(UserNameKey, signIn.UserName);
        Changed?.Invoke();
        MediaServers.NotifyChanged();
    }

    /// <summary>Signs out on the server (best effort) and forgets the token. The address is kept for next time.</summary>
    public static async System.Threading.Tasks.Task SignOutAsync()
    {
        if (CreateClient() is { } client)
            await client.SignOutAsync();
        if (ServerId is { Length: > 0 } serverId)
            CredentialStore.Delete(CredentialPrefix + serverId);
        AppStorage.SetString(ServerIdKey, null);
        AppStorage.SetString(ServerNameKey, null);
        AppStorage.SetString(UserIdKey, null);
        AppStorage.SetString(UserNameKey, null);
        Changed?.Invoke();
        MediaServers.NotifyChanged();
    }
}
