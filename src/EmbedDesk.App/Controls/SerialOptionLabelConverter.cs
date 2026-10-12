using System.Globalization;
using System.IO.Ports;
using System.Windows.Data;

namespace EmbedDesk.App.Controls;

public sealed class SerialOptionLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Parity.None => "N · 无校验", Parity.Odd => "O · 奇校验", Parity.Even => "E · 偶校验", Parity.Mark => "M · 标记", Parity.Space => "S · 空格",
        StopBits.One => "1", StopBits.OnePointFive => "1.5", StopBits.Two => "2",
        Handshake.None => "无流控", Handshake.RequestToSend => "RTS / CTS", Handshake.XOnXOff => "XON / XOFF", Handshake.RequestToSendXOnXOff => "RTS/CTS + XON/XOFF",
        _ => value
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
