using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace NzbWebDAV.Utils;

public static class PasswordUtil
{
    private static readonly MemoryCache Cache = new(new MemoryCacheOptions() { SizeLimit = 5 });
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly byte[] CacheKeySecret = RandomNumberGenerator.GetBytes(32);

    // Used when the account/user is missing so Verify still runs (closes username-enumeration timing).
    private static readonly string DummyHash = Hasher.HashPassword(null!, "nzbdav-dummy-password");

    public static string Hash(string password, string salt = "")
    {
        return Hasher.HashPassword(null!, password + salt);
    }

    public static bool Verify(string hash, string password, string salt = "")
    {
        // If users forget to add the "--use-cookies" argument to Rclone, then Rclone will not store
        // session cookies, which means the Authorization header from HTTP Basic Auth will be sent and
        // validated on every single request. This means the password from the Authorization header will
        // get hashed on every single request in order to compare it against the hashed password in the
        // database. Password hashing is intentionally designed to be super slow in order to slow down brute
        // force attacks. Several hundred milliseconds would be added to every single webdav request
        // when the "--use-cookies" Rclone argument is not used, if not for the memory cache added here.
        var cacheKey = CreateCacheKey(hash, password, salt);
        return Cache.GetOrCreate(cacheKey, cacheEntry =>
        {
            cacheEntry.Size = 1;
            cacheEntry.SetSlidingExpiration(TimeSpan.FromMinutes(5));
            return Hasher.VerifyHashedPassword(null!, hash, password + salt);
        }) == PasswordVerificationResult.Success;
    }

    /// <summary>
    /// Runs the same PBKDF2 verify path against a static dummy hash and discards the result.
    /// Call this when the account/user does not exist so timing matches a failed password check.
    /// </summary>
    public static void VerifyDummy(string password, string salt = "")
    {
        _ = Verify(DummyHash, password, salt);
    }

    private static string CreateCacheKey(string hash, string password, string salt)
    {
        var keyBytes = Encoding.UTF8.GetBytes(string.Concat(hash, "\0", password, "\0", salt));
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(CacheKeySecret, keyBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }
}
