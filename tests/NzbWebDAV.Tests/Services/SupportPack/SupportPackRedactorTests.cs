using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Services.SupportPack;

namespace NzbWebDAV.Tests.Services.SupportPack;

public class SupportPackRedactorTests
{
    [Fact]
    public void RedactText_RedactsLiteralEncodedUrlSecretsAndPseudonymizesAddresses()
    {
        var redactor = new SupportPackRedactor(["top secret", "api-secret"]);

        var result = redactor.RedactText(
            "GET https://user:pass@example.test/nzb?apikey=api-secret&token=abc " +
            "from 192.0.2.10 and [2001:db8::1]; repeated 192.0.2.10; encoded top%20secret");

        Assert.DoesNotContain("api-secret", result);
        Assert.DoesNotContain("top%20secret", result);
        Assert.DoesNotContain("user:pass@", result);
        Assert.DoesNotContain("192.0.2.10", result);
        Assert.DoesNotContain("2001:db8::1", result);
        Assert.Contains("apikey=[REDACTED]", result);
        Assert.Contains("token=[REDACTED]", result);
        Assert.Equal(2, redactor.AddressesPseudonymized);
    }

    [Fact]
    public void RedactConfigurationValue_RedactsKnownStructuredSecrets()
    {
        var redactor = new SupportPackRedactor([]);
        var result = redactor.RedactConfigurationValue(
            ConfigKeys.UsenetProviders,
            """{"Providers":[{"Host":"news.example","User":"alice","Pass":"provider-secret"}]}""");

        using var document = JsonDocument.Parse(result);
        var provider = document.RootElement.GetProperty("Providers")[0];
        Assert.Equal("news.example", provider.GetProperty("Host").GetString());
        Assert.Equal("alice", provider.GetProperty("User").GetString());
        Assert.Equal("[REDACTED]", provider.GetProperty("Pass").GetString());
    }

    [Fact]
    public void RedactConfigurationValue_FailsClosedForMalformedStructuredConfig()
    {
        var redactor = new SupportPackRedactor([]);

        var result = redactor.RedactConfigurationValue(ConfigKeys.IndexersInstances, "{not-json");

        Assert.Equal("[REDACTED_MALFORMED_STRUCTURED_VALUE]", result);
    }
}
