using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class HealthCheckTransportFailureMessageTests
{
    [Fact]
    public void FormatTransportFailureHealthMessage_IncludesReason()
    {
        var message = HealthCheckService.FormatTransportFailureHealthMessage(
            "Timeout reading from NNTP stream.");

        Assert.Equal(
            "NNTP transport failure during health check: Timeout reading from NNTP stream.",
            message);
    }
}
