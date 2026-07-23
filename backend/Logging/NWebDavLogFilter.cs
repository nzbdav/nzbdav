using Serilog.Events;

namespace NzbWebDAV.Logging;

internal static class NWebDavLogFilter
{
    private const string PropFindHandlerSource = "NWebDav.Server.Handlers.PropFindHandler";
    private const string PropertyErrorPrefix = "Property ";

    internal static bool IsCancelledPropFindPropertyError(LogEvent logEvent)
    {
        return logEvent.Exception is OperationCanceledException
               && logEvent.Properties.TryGetValue("SourceContext", out var sourceContext)
               && sourceContext is ScalarValue { Value: string source }
               && string.Equals(source, PropFindHandlerSource, StringComparison.Ordinal)
               && logEvent.MessageTemplate.Text.StartsWith(PropertyErrorPrefix, StringComparison.Ordinal);
    }
}
