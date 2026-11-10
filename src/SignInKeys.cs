using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

/// <summary>
/// Where the sign-in cookie keys live and how they are encrypted at rest, shared word for word by TaskList, TaskList
/// Stats, NewsReview, and SpendTracker. The keys always live in data/keys. On Windows they are encrypted with
/// machine-scope DPAPI. On Linux they are encrypted with AES-GCM under a secret that systemd decrypts and hands only to
/// the service (an encrypted credential, tied to the machine's credential key or its TPM, imported by the systemd
/// template): a copied data folder or backup is then useless on its own, as on Windows. Without that credential (say,
/// the app started from a terminal) the key files are left unencrypted, readable only by their owner.
/// </summary>
static class SignInKeys
{
    // The secret's file in the folder systemd puts credentials in ($CREDENTIALS_DIRECTORY).
    const string CredentialSuffix = "-sign-in-keys";

    /// <summary>
    /// Sets up the app's Data Protection with its keys in keysDirectory (looked up once the app has its configuration,
    /// as NewsReview finds its data folder), encrypted as described above.
    /// </summary>
    public static void Configure(IServiceCollection services, string applicationName, Func<IServiceProvider, string> keysDirectory, string credentialName)
    {
        services.AddDataProtection().SetApplicationName(applicationName);
        var secret = OperatingSystem.IsWindows() ? null : ReadCredential(credentialName + CredentialSuffix);
        CredentialXmlDecryptor.Secret = secret;
        services.AddOptions<KeyManagementOptions>().Configure<IServiceProvider>((options, provider) =>
        {
            var loggers = provider.GetRequiredService<ILoggerFactory>();
            var directory = keysDirectory(provider);
            // On Linux only the account the app runs as can open the folder, even when started outside the systemd template.
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(directory), loggers);
            if (OperatingSystem.IsWindows()) options.XmlEncryptor = new DpapiXmlEncryptor(protectToLocalMachine: true, loggers);
            else if (secret is not null)
            {
                options.XmlEncryptor = new CredentialXmlEncryptor(secret);
                EncryptExistingKeys(directory, options.XmlEncryptor);
            }
        });
    }

    static readonly XNamespace DataProtectionNamespace = "http://schemas.asp.net/2015/03/dataProtection";

    /// <summary>
    /// Keys written before the credential existed are still plain; their secrets are encrypted in place, in the form
    /// ASP.NET writes itself, so a copied data folder holds no readable key and nobody is signed out.
    /// </summary>
    static void EncryptExistingKeys(string directory, IXmlEncryptor encryptor)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "key-*.xml"))
        {
            var document = XDocument.Load(path);
            var plain = document.Descendants().Where(element => (string?)element.Attribute(DataProtectionNamespace + "requiresEncryption") == "true").ToList();
            if (plain.Count == 0) continue;
            foreach (var element in plain)
                element.ReplaceWith(new XElement(DataProtectionNamespace + "encryptedSecret",
                    new XAttribute("decryptorType", typeof(CredentialXmlDecryptor).AssemblyQualifiedName!),
                    encryptor.Encrypt(element).EncryptedElement));
            // Written beside the key and renamed over it, so a crash cannot leave half a key.
            var temporary = path + ".tmp";
            document.Save(temporary);
            File.Move(temporary, path, overwrite: true);
        }
    }

    /// <summary>Where the keys are and how they are encrypted, for the startup log.</summary>
    public static string Describe(string keysDirectory, string credentialName)
    {
        if (OperatingSystem.IsWindows()) return $"{keysDirectory}, encrypted with machine-scope DPAPI";
        return CredentialXmlDecryptor.Secret is null
            ? $"{keysDirectory}, not encrypted (owner-only files; the {credentialName}{CredentialSuffix} systemd credential would encrypt them, see the README)"
            : $"{keysDirectory}, encrypted with the {credentialName}{CredentialSuffix} systemd credential";
    }

    /// <summary>The AES key derived from the credential systemd passed in, or null without one.</summary>
    static byte[]? ReadCredential(string name)
    {
        var folder = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        if (string.IsNullOrEmpty(folder)) return null;
        var path = Path.Combine(folder, name);
        if (!File.Exists(path)) return null;
        var raw = File.ReadAllBytes(path);
        if (raw.Length < 16) throw new InvalidOperationException($"The {name} credential is too short; create it from 32 random bytes as the README shows.");
        // Any length of random secret becomes one 256-bit key.
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, raw, 32, info: "my-services sign-in keys"u8.ToArray());
    }

    const int NonceSize = 12;
    const int TagSize = 16;

    sealed class CredentialXmlEncryptor(byte[] secret) : IXmlEncryptor
    {
        public EncryptedXmlInfo Encrypt(XElement plaintextElement)
        {
            var plaintext = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
            var sealedBytes = new byte[NonceSize + TagSize + plaintext.Length];
            var nonce = sealedBytes.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(secret, TagSize);
            aes.Encrypt(nonce, plaintext, sealedBytes.AsSpan(NonceSize + TagSize), sealedBytes.AsSpan(NonceSize, TagSize));
            var element = new XElement("encryptedKey",
                new XComment(" Encrypted with the systemd sign-in-keys credential (AES-256-GCM: nonce, tag, ciphertext). "),
                new XElement("value", Convert.ToBase64String(sealedBytes)));
            return new EncryptedXmlInfo(element, typeof(CredentialXmlDecryptor));
        }
    }

    /// <summary>Created by ASP.NET by type when it reads a key, so the secret is handed over through a static.</summary>
    public sealed class CredentialXmlDecryptor : IXmlDecryptor
    {
        internal static byte[]? Secret;

        public XElement Decrypt(XElement encryptedElement)
        {
            var secret = Secret ?? throw new InvalidOperationException(
                "This sign-in key was encrypted with a systemd credential that this run does not have. Start the app through its systemd service, or delete data/keys and sign in again.");
            var sealedBytes = Convert.FromBase64String((string?)encryptedElement.Element("value") ?? string.Empty);
            if (sealedBytes.Length < NonceSize + TagSize) throw new CryptographicException("The sign-in key file is damaged.");
            var plaintext = new byte[sealedBytes.Length - NonceSize - TagSize];
            using var aes = new AesGcm(secret, TagSize);
            aes.Decrypt(sealedBytes.AsSpan(0, NonceSize), sealedBytes.AsSpan(NonceSize + TagSize), sealedBytes.AsSpan(NonceSize, TagSize), plaintext);
            return XElement.Parse(System.Text.Encoding.UTF8.GetString(plaintext));
        }
    }
}
