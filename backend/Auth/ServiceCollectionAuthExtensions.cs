using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NWebDav.Server;
using NWebDav.Server.Authentication;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Auth;

public static class ServiceCollectionAuthExtensions
{
    private static readonly byte[] CredentialCacheKey = RandomNumberGenerator.GetBytes(32);
    private static readonly MemoryCache VerifiedCredentials = new(new MemoryCacheOptions
    {
        SizeLimit = 16
    });

    public static IServiceCollection AddWebdavBasicAuthentication
    (
        this IServiceCollection services,
        ConfigManager configManager
    )
    {
        // no-op when webdav auth is disabled
        if (WebApplicationAuthExtensions.IsWebdavAuthDisabled())
            return services;

        // otherwise configure basic auth
        services
            .AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Join(DavDatabaseContext.ConfigPath, "data-protection")));
        services
            .AddAuthentication(opts => opts.DefaultScheme = BasicAuthenticationDefaults.AuthenticationScheme)
            .AddBasicAuthentication(opts =>
            {
                opts.AllowInsecureProtocol = true;
                opts.CacheCookieName = "nzb-webdav-backend";
                opts.CacheCookieExpiration = TimeSpan.FromHours(1);
                opts.Events.OnValidateCredentials = (ValidateCredentialsContext context) =>
                    ValidateCredentials(context, configManager);
            });

        return services;
    }

    private static Task ValidateCredentials(ValidateCredentialsContext context, ConfigManager configManager)
    {
        var user = configManager.GetWebdavUser();
        var passwordHash = configManager.GetWebdavPasswordHash();

        if (user == null || passwordHash == null)
        {
            context.Fail("webdav user and password are not yet configured.");
            return Task.CompletedTask;
        }

        if (context.Username == user &&
            VerifyPasswordWithCache(context.Username, context.Password, passwordHash))
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, context.Username, ClaimValueTypes.String,
                    context.Options.ClaimsIssuer),
                new Claim(ClaimTypes.Name, context.Username, ClaimValueTypes.String,
                    context.Options.ClaimsIssuer)
            };

            context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, context.Scheme.Name));
            context.Success();
        }
        else
        {
            context.Fail("invalid credentials");
        }

        return Task.CompletedTask;
    }

    private static bool VerifyPasswordWithCache(string username, string password, string passwordHash)
    {
        var credentialBytes = Encoding.UTF8.GetBytes(
            string.Concat(username, "\0", passwordHash, "\0", password));
        try
        {
            var cacheKey = Convert.ToHexString(
                HMACSHA256.HashData(CredentialCacheKey, credentialBytes));
            if (VerifiedCredentials.TryGetValue(cacheKey, out _)) return true;
            if (!PasswordUtil.Verify(passwordHash, password)) return false;

            VerifiedCredentials.Set(cacheKey, true, new MemoryCacheEntryOptions()
                .SetSize(1)
                .SetSlidingExpiration(TimeSpan.FromMinutes(5)));
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credentialBytes);
        }
    }
}
