using System;
using System.Runtime.InteropServices;
using System.Text;

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
        CredentialStore.Write(CredentialPrefix + serverId, signIn.UserName, signIn.AccessToken);
        AppStorage.SetString(AddressKey, server.Address);
        AppStorage.SetString(ServerIdKey, serverId);
        AppStorage.SetString(ServerNameKey, server.Name);
        AppStorage.SetString(UserIdKey, signIn.UserId);
        AppStorage.SetString(UserNameKey, signIn.UserName);
        Changed?.Invoke();
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
    }

    /// <summary>Windows Credential Manager (generic credentials), via advapi32. Works for unpackaged apps.</summary>
    private static class CredentialStore
    {
        private const int GenericType = 1;
        private const int PersistLocalMachine = 2;

        public static void Write(string target, string userName, string secret)
        {
            var bytes = Encoding.Unicode.GetBytes(secret);
            var blob = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new Credential
                {
                    Type = GenericType,
                    TargetName = target,
                    UserName = userName,
                    CredentialBlob = blob,
                    CredentialBlobSize = bytes.Length,
                    Persist = PersistLocalMachine
                };
                if (!CredWrite(ref credential, 0))
                    throw new JellyfinException($"Windows couldn't save the sign-in (error {Marshal.GetLastWin32Error()}).");
            }
            finally
            {
                Marshal.FreeHGlobal(blob);
            }
        }

        public static string? Read(string target)
        {
            if (!CredRead(target, GenericType, 0, out var pointer))
                return null;
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0)
                    return null;
                return Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
            }
            finally
            {
                CredFree(pointer);
            }
        }

        public static void Delete(string target) => CredDelete(target, GenericType, 0);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);
    }
}
