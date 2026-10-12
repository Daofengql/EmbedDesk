using System.Text;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;

namespace EmbedDesk.Infrastructure.Diagnostics;

public sealed class HttpDiagnosticsParser : IHttpDiagnosticsParser
{
    public HttpMessageInfo Parse(ReadOnlyMemory<byte> data)
    {
        var bytes = data.ToArray();
        var separator = FindHeaderSeparator(bytes);
        if (separator < 0)
        {
            return new HttpMessageInfo(null, Array.Empty<HttpHeader>(), data, false, null, null, null, null);
        }

        var headerText = Encoding.UTF8.GetString(bytes, 0, separator);
        var bodyStart = separator + (bytes.AsSpan(separator).StartsWith("\r\n\r\n"u8) ? 4 : 2);
        var lines = headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        if (lines.Length == 0)
        {
            return new HttpMessageInfo(null, Array.Empty<HttpHeader>(), data, false, null, null, null, null);
        }

        var startLine = lines[0].Trim();
        var headers = lines.Skip(1)
            .Select(line =>
            {
                var colon = line.IndexOf(':');
                return colon > 0 ? new HttpHeader(line[..colon].Trim(), line[(colon + 1)..].Trim()) : null;
            })
            .Where(header => header is not null)
            .Cast<HttpHeader>()
            .ToArray();

        var isRequest = !startLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase);
        string? method = null;
        string? path = null;
        string? protocol = null;
        int? statusCode = null;
        var parts = startLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (isRequest && parts.Length >= 3)
        {
            method = parts[0];
            path = parts[1];
            protocol = parts[2];
        }
        else if (!isRequest && parts.Length >= 2)
        {
            protocol = parts[0];
            if (int.TryParse(parts[1], out var parsedStatus)) statusCode = parsedStatus;
        }

        var body = bodyStart < bytes.Length ? bytes.AsMemory(bodyStart) : ReadOnlyMemory<byte>.Empty;
        return new HttpMessageInfo(startLine, headers, body, isRequest, method, path, protocol, statusCode);
    }

    private static int FindHeaderSeparator(byte[] data)
    {
        for (var index = 0; index < data.Length - 1; index++)
        {
            if (index + 4 <= data.Length && data[index] == '\r' && data[index + 1] == '\n' && data[index + 2] == '\r' && data[index + 3] == '\n') return index;
            if (data[index] == '\n' && data[index + 1] == '\n') return index;
        }
        return -1;
    }
}
