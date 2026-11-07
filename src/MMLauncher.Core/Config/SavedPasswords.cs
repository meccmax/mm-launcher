using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace MMLauncher.Core.Config;

/// <summary>
/// Server passwords the player chose to remember, stored as saved-passwords.jsonc. Each one is
/// encrypted with Windows' own per-user protection (DPAPI), so the file is useless on another PC
/// or to another Windows account; only this Windows user can read the passwords back. Nothing is
/// saved unless the player ticks "Remember password", and nothing is ever sent anywhere.
/// </summary>
public sealed class SavedPasswords
{
    /// <summary>"host:queryport" → the encrypted password (base64).</summary>
    public Dictionary<string, string> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public const int MaxLength = 128;
    public const int MaxServers = 200;

    /// <summary>Encrypt / decrypt. Windows DPAPI by default; self-checks swap in their own. Null = saving isn't possible here.</summary>
    public static Func<byte[], byte[]>? Protect { get; set; } = OperatingSystem.IsWindows() ? Dpapi.Protect : null;
    public static Func<byte[], byte[]>? Unprotect { get; set; } = OperatingSystem.IsWindows() ? Dpapi.Unprotect : null;

    /// <summary>False where passwords can't be encrypted (not Windows), so "Remember" isn't offered.</summary>
    public static bool CanSave => Protect != null && Unprotect != null;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string FilePath => Path.Combine(LauncherSettings.DataFolder, "saved-passwords.jsonc");

    private static SavedPasswords? _current;
    public static SavedPasswords Current => _current ??= Load();

    public static SavedPasswords Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<SavedPasswords>(File.ReadAllText(FilePath), ReadOptions) is { } s)
            {
                s.Servers = new Dictionary<string, string>(s.Servers ?? new(), StringComparer.OrdinalIgnoreCase);
                return s;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return new SavedPasswords();
    }

    public static string Key(string host, int queryPort) => $"{host.Trim().ToLowerInvariant()}:{queryPort}";

    public bool Has(string host, int queryPort) => Servers.ContainsKey(Key(host, queryPort));

    /// <summary>The saved password, or null if none is saved (or it can't be decrypted, e.g. the file came from another PC).</summary>
    public string? Get(string host, int queryPort)
    {
        if (Unprotect == null || !Servers.TryGetValue(Key(host, queryPort), out var stored)) return null;
        try { return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(stored))); }
        catch (Exception) { return null; }
    }

    /// <summary>Saves (encrypted) and writes the file. False if it can't be encrypted here or is empty / too long.</summary>
    public bool Set(string host, int queryPort, string password)
    {
        if (Protect == null || password.Length is 0 or > MaxLength) return false;
        try
        {
            var key = Key(host, queryPort);
            Servers[key] = Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(password)));
            if (Servers.Count > MaxServers)
                foreach (var extra in Servers.Keys.Where(k => k != key).Take(Servers.Count - MaxServers).ToList()) Servers.Remove(extra);
            return Save();
        }
        catch (Exception) { return false; }
    }

    public bool Remove(string host, int queryPort) => Servers.Remove(Key(host, queryPort)) && Save();

    private bool Save()
    {
        try
        {
            Directory.CreateDirectory(LauncherSettings.DataFolder);
            var sb = new StringBuilder();
            sb.AppendLine("// MM Launcher: server passwords you chose to remember (\"Remember password\" when joining).");
            sb.AppendLine("// Servers: server address (host:query port) -> its password, encrypted with Windows' own");
            sb.AppendLine("// protection for your Windows account. Only you, on this PC, can read them back; copying this");
            sb.AppendLine("// file elsewhere doesn't reveal them. Forget one in the launcher (server panel > Forget), or");
            sb.AppendLine("// delete this file to forget them all.");
            sb.Append(JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            sb.AppendLine();
            File.WriteAllText(FilePath, sb.ToString());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Windows DPAPI (CryptProtectData), current user only, with a launcher-specific extra key.</summary>
    [SupportedOSPlatform("windows")]
    private static class Dpapi
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MM Launcher saved server password");

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob { public int Size; public IntPtr Data; }

        private const int UiForbidden = 0x1;

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob dataIn, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        public static byte[] Protect(byte[] data) => Run(data, encrypt: true);
        public static byte[] Unprotect(byte[] data) => Run(data, encrypt: false);

        private static byte[] Run(byte[] data, bool encrypt)
        {
            var inHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
            var entHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            try
            {
                var input = new Blob { Size = data.Length, Data = inHandle.AddrOfPinnedObject() };
                var entropy = new Blob { Size = Entropy.Length, Data = entHandle.AddrOfPinnedObject() };
                bool ok = encrypt
                    ? CryptProtectData(ref input, "MM Launcher", ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok) throw new InvalidOperationException($"Windows couldn't {(encrypt ? "encrypt" : "decrypt")} the password (error {Marshal.GetLastWin32Error()}).");
                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return result;
                }
                finally { LocalFree(output.Data); }
            }
            finally
            {
                inHandle.Free();
                entHandle.Free();
            }
        }
    }
}
