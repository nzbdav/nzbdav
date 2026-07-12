namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Disposable wrapper that automatically returns a borrowed connection to the
/// originating <see cref="ConnectionPool{T}"/>.
///
/// Note: This class was authored by ChatGPT 3o
/// </summary>
public sealed class ConnectionLock<T> : IDisposable
{
    private readonly Action<T> _syncReturn;
    private readonly Action<T> _syncDestroy;
    private T? _connection;
    private int _disposed; // 0 == false, 1 == true
    private int _replace; // 0 == false, 1 == true

    internal ConnectionLock
    (
        T connection,
        Action<T> syncReturn,
        Action<T> syncDestroy,
        bool wasReused
    )
    {
        _connection = connection;
        _syncReturn = syncReturn;
        _syncDestroy = syncDestroy;
        WasReused = wasReused;
    }

    public T Connection
        => _connection ?? throw new ObjectDisposedException(nameof(ConnectionLock<T>));

    /// <summary>
    /// True when the connection was taken from the idle pool rather than freshly
    /// created. A reused connection may have been closed server-side while idle,
    /// so its first failure should not count against provider health or retry budgets.
    /// </summary>
    public bool WasReused { get; }

    /// <summary>
    /// Marks the underlying connection to be replaced. When this lock is disposed,
    /// the underlying connection will be destroyed instead of returned to the pool.
    /// </summary>
    public void Replace()
    {
        Volatile.Write(ref _replace, 1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return; // already done
        var conn = Interlocked.Exchange(ref _connection, default);
        if (conn is not null)
        {
            var replace = Volatile.Read(ref _replace) == 1;
            if (replace)
                _syncDestroy(conn);
            else
                _syncReturn(conn);
        }

        GC.SuppressFinalize(this);
    }
}
