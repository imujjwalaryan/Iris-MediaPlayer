using System;

namespace Iris_MediaPlayer.Services.Stash;

/// <summary>
/// The Stash server Iris is connected to, kept between runs. The address and name live in Iris's
/// settings; the API key (if Stash asks for one) lives in Windows Credential Manager under
/// "Iris/Stash", never in Iris's own files. Use on the UI thread.
/// </summary>
internal static class StashSession
{
    private const string AddressKey = "StashAddress";
    private const string NameKey = "StashName";
    private const string HasKeyKey = "StashHasApiKey";
    private const string CredentialTarget = "Iris/Stash";

    /// <summary>Raised after connecting or disconnecting.</summary>
    public static event Action? Changed;

    public static string? Address => AppStorage.GetString(AddressKey);

    public static bool IsConnected => !string.IsNullOrEmpty(Address);

    /// <summary>What the server chip shows: the name given when connecting, or "Stash".</summary>
    public static string ServerName => AppStorage.GetString(NameKey) is { Length: > 0 } name ? name : "Stash";

    /// <summary>A client for the saved connection, or null when not connected.</summary>
    public static StashClient? CreateClient()
    {
        var address = Address;
        if (string.IsNullOrEmpty(address))
            return null;
        var key = AppStorage.GetBool(HasKeyKey) == true ? CredentialStore.Read(CredentialTarget) : null;
        return new StashClient(address, key);
    }

    public static void Save(StashClient client, string? name)
    {
        if (client.ApiKey is { } key)
            CredentialStore.Write(CredentialTarget, "Stash", key);
        else
            CredentialStore.Delete(CredentialTarget);
        AppStorage.SetBool(HasKeyKey, client.ApiKey is not null);
        AppStorage.SetString(AddressKey, client.Address);
        AppStorage.SetString(NameKey, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        Changed?.Invoke();
        MediaServers.NotifyChanged();
    }

    /// <summary>Forgets the server and its key. Nothing changes on the Stash server itself.</summary>
    public static void Disconnect()
    {
        CredentialStore.Delete(CredentialTarget);
        AppStorage.SetBool(HasKeyKey, false);
        AppStorage.SetString(AddressKey, null);
        AppStorage.SetString(NameKey, null);
        Changed?.Invoke();
        MediaServers.NotifyChanged();
    }
}
