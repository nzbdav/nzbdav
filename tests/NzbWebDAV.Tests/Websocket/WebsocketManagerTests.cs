using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Websocket;

public class WebsocketManagerTests
{
    [Fact]
    public async Task SendMessage_DoesNotLetSlowSocketBlockFastSocket()
    {
        var manager = new WebsocketManager();
        using var slowSocket = new TestWebSocket(blockSends: true);
        using var fastSocket = new TestWebSocket();
        var detachSlow = manager.AttachAuthenticatedSocketForTests(slowSocket);
        var detachFast = manager.AttachAuthenticatedSocketForTests(fastSocket);

        try
        {
            var broadcast = manager.SendMessage(WebsocketTopic.LiveStats, "current");

            await broadcast.WaitAsync(TimeSpan.FromSeconds(1));
            await slowSocket.SendStarted.WaitAsync(TimeSpan.FromSeconds(1));
            await WaitUntil(() => fastSocket.Messages.Count == 1);

            Assert.Empty(slowSocket.Messages);
            Assert.Equal("current", Parse(fastSocket.Messages.Single()).Message);
        }
        finally
        {
            await detachSlow();
            await detachFast();
        }
    }

    [Fact]
    public async Task StateMessages_CoalescePerTopic()
    {
        var manager = new WebsocketManager();
        using var socket = new TestWebSocket(blockSends: true);
        var detach = manager.AttachAuthenticatedSocketForTests(socket);

        try
        {
            await manager.SendMessage(WebsocketTopic.LiveStats, "initial");
            await socket.SendStarted.WaitAsync(TimeSpan.FromSeconds(1));

            for (var i = 0; i < 10; i++)
                await manager.SendMessage(WebsocketTopic.LiveStats, $"stale-{i}");
            await manager.SendMessage(WebsocketTopic.LiveStats, "latest");
            await manager.SendMessage(WebsocketTopic.UsenetConnections, "connections");

            socket.ReleaseSends();
            await WaitUntil(() => socket.Messages.Count == 3);

            var messages = socket.Messages.Select(Parse).ToList();
            Assert.Equal(
                new[] { "initial", "latest" },
                messages.Where(x => x.Topic == WebsocketTopic.LiveStats.Name).Select(x => x.Message));
            Assert.Equal(
                ["connections"],
                messages.Where(x => x.Topic == WebsocketTopic.UsenetConnections.Name).Select(x => x.Message));
        }
        finally
        {
            await detach();
        }
    }

    [Fact]
    public async Task EventQueueOverflow_DropsOldestEventsAndKeepsSocketConnected()
    {
        var manager = new WebsocketManager();
        using var socket = new TestWebSocket(blockSends: true);
        var detach = manager.AttachAuthenticatedSocketForTests(socket);

        try
        {
            await manager.SendMessage(WebsocketTopic.LiveStats, "blocked");
            await socket.SendStarted.WaitAsync(TimeSpan.FromSeconds(1));

            for (var i = 0; i <= 64; i++)
                await manager.SendMessage(WebsocketTopic.QueueItemAdded, $"event-{i}");

            socket.ReleaseSends();
            await WaitUntil(() => socket.Messages.Count == 65);

            Assert.False(socket.Aborted);
            Assert.Equal(1, manager.GetAuthenticatedSocketCount());
            var messages = socket.Messages.Select(Parse).ToList();
            Assert.Equal("blocked", messages[0].Message);
            Assert.Equal(
                Enumerable.Range(1, 64).Select(i => $"event-{i}"),
                messages.Skip(1).Select(x => x.Message));
        }
        finally
        {
            await detach();
        }
    }

    [Fact]
    public async Task SendMessage_UpdatesLastMessageWithoutConnectedSockets()
    {
        var manager = new WebsocketManager();

        await manager.SendMessage(WebsocketTopic.LiveStats, "latest");

        Assert.Equal("latest", manager.PeekLastMessage(WebsocketTopic.LiveStats));
    }

    [Fact]
    public async Task NewSocket_ReplaysLatestStateButNotEvents()
    {
        var manager = new WebsocketManager();
        await manager.SendMessage(WebsocketTopic.LiveStats, "latest");
        await manager.SendMessage(WebsocketTopic.QueueItemAdded, "old-event");
        using var socket = new TestWebSocket();
        var detach = manager.AttachAuthenticatedSocketForTests(socket, replayState: true);

        try
        {
            await WaitUntil(() => socket.Messages.Count == 1);

            var replay = Parse(socket.Messages.Single());
            Assert.Equal(WebsocketTopic.LiveStats.Name, replay.Topic);
            Assert.Equal("latest", replay.Message);
        }
        finally
        {
            await detach();
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static ReceivedMessage Parse(string rawMessage)
    {
        using var document = JsonDocument.Parse(rawMessage);
        return new ReceivedMessage(
            document.RootElement.GetProperty("Topic").GetString()!,
            document.RootElement.GetProperty("Message").GetString()!);
    }

    private sealed record ReceivedMessage(string Topic, string Message);

    private sealed class TestWebSocket(bool blockSends = false) : WebSocket
    {
        private readonly object _messagesLock = new();
        private readonly List<string> _messages = [];
        private readonly TaskCompletionSource<bool> _sendStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseSends =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        private int _aborted;

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messagesLock)
                    return _messages.ToList();
            }
        }

        public Task SendStarted => _sendStarted.Task;
        public bool Aborted => Volatile.Read(ref _aborted) != 0;
        public override WebSocketCloseStatus? CloseStatus { get; }
        public override string? CloseStatusDescription { get; }
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void ReleaseSends()
        {
            _releaseSends.TrySetResult(true);
        }

        public override void Abort()
        {
            Interlocked.Exchange(ref _aborted, 1);
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Receive unexpectedly completed");
        }

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            _sendStarted.TrySetResult(true);
            if (blockSends)
                await _releaseSends.Task.WaitAsync(cancellationToken);

            var message = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
            lock (_messagesLock)
                _messages.Add(message);
        }
    }
}
