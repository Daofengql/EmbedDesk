using System.Text;

namespace EmbeddedDebugger.App.ViewModels;

internal static class SendPayload
{
    public static byte[] Encode(string text, bool asHex, bool appendCrLf) => asHex
        ? Convert.FromHexString(string.Concat(text.Where(character => !char.IsWhiteSpace(character))))
        : Encoding.UTF8.GetBytes(appendCrLf ? text + "\r\n" : text);
}
