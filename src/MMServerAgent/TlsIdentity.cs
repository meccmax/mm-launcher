using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MMLauncher.Core.Agent;

namespace MMServerAgent;

/// <summary>
/// The agent's encryption key. Made once, the first time the agent starts, and kept in
/// agent-tls.pfx next to the exe (the install locks that folder to admins). Launchers
/// recognise the agent by this key's fingerprint, so keep the file when moving or
/// updating the agent; deleting it makes a new key, and launchers will refuse the agent
/// until the published fingerprint is updated.
/// </summary>
public static class TlsIdentity
{
    public const string FileName = "agent-tls.pfx";
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static X509Certificate2 LoadOrCreate(string? path = null)
    {
        path ??= FilePath;
        if (File.Exists(path))
        {
            try { return Load(File.ReadAllBytes(path)); }
            catch (CryptographicException ex)
            {
                // Don't silently replace a key launchers already know: keep the old file.
                var keep = path + ".unreadable";
                File.Move(path, keep, overwrite: true);
                Console.Error.WriteLine($"{FileName} couldn't be read ({ex.Message}); kept it as {Path.GetFileName(keep)} and made a new key.");
            }
        }
        var pfx = Create();
        File.WriteAllBytes(path, pfx);
        return Load(pfx);
    }

    /// <summary>A new self-signed ECDSA P-256 certificate, as PFX bytes.</summary>
    public static byte[] Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=MM Server Agent", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server auth
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
        return cert.Export(X509ContentType.Pfx);
    }

    /// <summary>
    /// Loads PFX bytes with a real (temporary) key container: Windows' TLS (SChannel) can't
    /// use an in-memory-only ("ephemeral") key. The container is removed when the agent exits.
    /// </summary>
    public static X509Certificate2 Load(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);

    /// <summary>Just the fingerprint, without touching any key store (for "check").</summary>
    public static string? ReadFingerprint(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path)) return null;
        using var cert = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(path), null, X509KeyStorageFlags.EphemeralKeySet);
        return Fingerprint(cert);
    }

    public static string Fingerprint(X509Certificate2 cert) => AgentTrust.Fingerprint(cert);
}
