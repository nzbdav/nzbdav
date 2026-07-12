using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace NzbWebDAV.Utils;

public static class PasswordUtil
{
    private static readonly MemoryCache Cache = new(new MemoryCacheOptions() { SizeLimit = 5 });
    private static readonly PasswordHasher<object> Hasher = new();

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
        return Cache.GetOrCreate(new CacheKey(hash, password, salt), cacheEntry =>
        {
            cacheEntry.Size = 1;
            return Hasher.VerifyHashedPassword(null!, hash, password + salt);
        }) == PasswordVerificationResult.Success;
    }

    private record CacheKey(string Hash, string Password, string Salt);
}
