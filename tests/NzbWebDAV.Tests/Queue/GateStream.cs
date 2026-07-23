namespace NzbWebDAV.Tests.Queue;

/// <summary>
/// Stream whose first read waits on a gate so queue workers can be held in-flight.
/// After the gate opens it yields a tiny NZB payload once, then EOF.
/// </summary>
internal sealed class GateStream(ManualResetEventSlim gate) : Stream
{
    private static readonly byte[] Payload = "<nzb></nzb>"u8.ToArray();
    private int _phase; // 0 = waiting, 1 = yield payload, 2 = eof

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => 0;
        set => throw new NotSupportedException();
    }

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _phase) == 0)
        {
            while (!gate.IsSet)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.CompareExchange(ref _phase, 1, 0);
        }

        if (Interlocked.CompareExchange(ref _phase, 2, 1) == 1)
        {
            var n = Math.Min(count, Payload.Length);
            Buffer.BlockCopy(Payload, 0, buffer, offset, n);
            return n;
        }

        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
