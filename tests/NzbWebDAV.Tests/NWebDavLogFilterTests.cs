using NzbWebDAV.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Logging;

public class NWebDavLogFilterTests
{
    private const string PropFindHandlerSource = "NWebDav.Server.Handlers.PropFindHandler";

    [Fact]
    public void CancelledPropFindPropertyError_IsExcluded()
    {
        var events = CaptureFilteredLogs(logger =>
            WritePropertyError(logger, PropFindHandlerSource, new OperationCanceledException()));

        Assert.Empty(events);
    }

    [Fact]
    public void NonCancellationPropertyError_IsRetained()
    {
        var events = CaptureFilteredLogs(logger =>
            WritePropertyError(logger, PropFindHandlerSource, new InvalidOperationException()));

        Assert.Single(events);
    }

    [Fact]
    public void CancelledPropertyErrorFromAnotherSource_IsRetained()
    {
        var events = CaptureFilteredLogs(logger =>
            WritePropertyError(logger, "NzbWebDAV.SomeComponent", new OperationCanceledException()));

        Assert.Single(events);
    }

    [Fact]
    public void OtherCancelledPropFindError_IsRetained()
    {
        var events = CaptureFilteredLogs(logger =>
            logger
                .ForContext("SourceContext", PropFindHandlerSource)
                .Error(new OperationCanceledException(), "PROPFIND response failed"));

        Assert.Single(events);
    }

    private static void WritePropertyError(ILogger logger, string source, Exception exception)
    {
        logger
            .ForContext("SourceContext", source)
            .Error(
                exception,
                "Property {PropertyName} on item {ItemName} raised an exception.",
                "{DAV:}displayname",
                "item");
    }

    private static IReadOnlyList<LogEvent> CaptureFilteredLogs(Action<ILogger> write)
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Filter.ByExcluding(NWebDavLogFilter.IsCancelledPropFindPropertyError)
            .WriteTo.Sink(sink)
            .CreateLogger();

        write(logger);
        return sink.Events;
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
