using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

/// <summary>
/// Password sign-in shared word for word by TaskList, TaskList Stats, NewsReview, and SpendTracker: the console-only
/// first-run setup token, the saved password verifier (data/auth.json), and the 30-day sign-in cookie.
/// </summary>
static class PasswordAuth
{
    public const int Iterations = 600_000;
    // Bounds the PBKDF2 work one sign-in attempt can cause.
    public const int MaxPasswordLength = 1024;
    // Reads files saved with either property casing (NewsReview's were lowercase), and writes PascalCase.
    static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    record PasswordFile(int Version, int Iterations, string Salt, string Hash);

    public static string GenerateSetupToken()
    {
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        return $"{raw[..4]}-{raw[4..8]}-{raw[8..12]}-{raw[12..16]}";
    }

    public static bool SetupTokenMatches(string? expected, string? supplied)
    {
        if (expected is null || string.IsNullOrWhiteSpace(supplied)) return false;
        var expectedValue = expected.Replace("-", string.Empty, StringComparison.Ordinal);
        var suppliedValue = Regex.Replace(supplied, @"[\s-]", string.Empty);
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expectedValue.ToUpperInvariant()),
            System.Text.Encoding.UTF8.GetBytes(suppliedValue.ToUpperInvariant()));
    }

    public static bool IsConfigured(string authPath) => File.Exists(authPath);

    public static string? ValidateNewPassword(string? password, string? confirmation)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            return "Password must be at least 8 characters long.";
        if (password.Length > MaxPasswordLength)
            return $"Password must be at most {MaxPasswordLength} characters long.";
        if (password != confirmation)
            return "Passwords do not match.";
        return null;
    }

    /// <summary>Saves a new verifier, readable only by this account on Linux and macOS.</summary>
    public static void Save(string authPath, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var auth = new PasswordFile(1, Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(authPath))!);
        // Write beside the target and rename, so an interrupted rehash cannot leave a truncated auth.json.
        var temporaryPath = authPath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporaryPath, options))
        {
            JsonSerializer.Serialize(stream, auth);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, authPath, overwrite: true);
    }

    /// <summary>Checks the password. A missing or unreadable auth.json fails closed; needsRehash asks for a save at today's iterations.</summary>
    public static bool Verify(string authPath, string? password, out bool needsRehash)
    {
        needsRehash = false;
        if (string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength) return false;
        try
        {
            var auth = JsonSerializer.Deserialize<PasswordFile>(File.ReadAllText(authPath), ReadOptions);
            if (auth is null || auth.Version != 1 || auth.Iterations < 1) return false;
            var salt = Convert.FromBase64String(auth.Salt);
            var expected = Convert.FromBase64String(auth.Hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, auth.Iterations, HashAlgorithmName.SHA256, expected.Length);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return false;
            needsRehash = auth.Iterations < Iterations;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task SignInOwner(HttpContext context)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Name, "Owner")], CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true, AllowRefresh = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
    }
}
