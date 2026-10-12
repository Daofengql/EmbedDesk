using EmbedDesk.Core.Models;

namespace EmbedDesk.Core.Abstractions;

public interface IHttpDiagnosticsParser
{
    HttpMessageInfo Parse(ReadOnlyMemory<byte> data);
}

public interface IAppLogSink
{
    void Add(string category, string message, TransportEventKind kind = TransportEventKind.Info);
}
