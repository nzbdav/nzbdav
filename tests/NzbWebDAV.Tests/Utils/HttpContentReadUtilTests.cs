using System.Net;
using System.Text;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class HttpContentReadUtilTests
{
    private const long Limit = 16;

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    public async Task ReadBoundedAsync_AcceptsBodiesAtOrBelowLimit(int size)
    {
        var payload = Enumerable.Repeat((byte)'a', size).ToArray();
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentLength = payload.Length;

        var bytes = await HttpContentReadUtil.ReadBoundedAsync(content, Limit, CancellationToken.None);

        Assert.Equal(payload, bytes);
    }

    [Fact]
    public async Task ReadBoundedAsync_RejectsDeclaredContentLengthAboveLimit()
    {
        using var content = new ByteArrayContent(new byte[Limit + 1]);
        content.Headers.ContentLength = Limit + 1;

        var ex = await Assert.ThrowsAsync<NzbResponseTooLargeException>(
            () => HttpContentReadUtil.ReadBoundedAsync(content, Limit, CancellationToken.None));

        Assert.Equal(Limit, ex.MaxBytes);
        Assert.Equal(Limit + 1, ex.ContentLength);
    }

    [Fact]
    public async Task ReadBoundedAsync_RejectsStreamedBodyAboveLimitWithoutPartialReturn()
    {
        using var content = new UndeclaredLengthContent(new byte[Limit + 1]);

        var ex = await Assert.ThrowsAsync<NzbResponseTooLargeException>(
            () => HttpContentReadUtil.ReadBoundedAsync(content, Limit, CancellationToken.None));

        Assert.Equal(Limit, ex.MaxBytes);
        Assert.Null(ex.ContentLength);
    }

    [Fact]
    public async Task ReadBoundedAsync_AcceptsUndeclaredBodyAtLimit()
    {
        var payload = Encoding.ASCII.GetBytes("0123456789abcdef");
        Assert.Equal(Limit, payload.Length);
        using var content = new UndeclaredLengthContent(payload);

        var bytes = await HttpContentReadUtil.ReadBoundedAsync(content, Limit, CancellationToken.None);

        Assert.Equal(payload, bytes);
    }

    private sealed class UndeclaredLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(payload, 0, payload.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
