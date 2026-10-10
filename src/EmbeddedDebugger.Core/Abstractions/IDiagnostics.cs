using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.Core.Abstractions;

public interface IHttpDiagnosticsParser
{
    HttpMessageInfo Parse(ReadOnlyMemory<byte> data);
}

public interface IAppLogSink
{
    void Add(string category, string message, TransportEventKind kind = TransportEventKind.Info);
}
