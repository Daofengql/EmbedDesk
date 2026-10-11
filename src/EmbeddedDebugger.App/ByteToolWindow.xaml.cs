using System.IO;
using System.Text;
using System.Windows;
using EmbeddedDebugger.Infrastructure.Diagnostics;
using Microsoft.Win32;

namespace EmbeddedDebugger.App;

public partial class ByteToolWindow : Window
{
    private readonly bool _checksum;
    private byte[]? _fileBytes;
    private bool _closed;
    private bool _busy;
    private static readonly string[] FormatNames = ["HEX 字节", "UTF-8 文本", "ASCII 文本", "UTF-16 LE 文本", "Base64"];

    public ByteToolWindow(bool checksum = false)
    {
        InitializeComponent();
        _checksum = checksum;
        Title = TitleText.Text = checksum ? "CRC / 校验和" : "HEX / 文本编解码";
        DescriptionText.Text = checksum ? "串口报文与固件校验 · 显示算法参数、标准十六进制值和低字节在前的发送顺序。" : "HEX、Base64 与 UTF-8 / ASCII / UTF-16 LE 互转；无效字节会报告错误。";
        OutputFormatPanel.Visibility = checksum ? Visibility.Collapsed : Visibility.Visible;
        RunButton.Content = checksum ? "计算" : "转换";
        InputFormatBox.ItemsSource = OutputFormatBox.ItemsSource = FormatNames;
        InputFormatBox.SelectedIndex = 1; OutputFormatBox.SelectedIndex = 0;
        InputBox.Text = checksum ? "123456789" : "Hello, 你好";
        WindowTheme.ApplyDarkTitleBar(this);
        Closed += (_, _) => _closed = true;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var input = InputBox.Text;
        var source = (ByteFormat)InputFormatBox.SelectedIndex;
        var destination = (ByteFormat)OutputFormatBox.SelectedIndex;
        var file = _fileBytes;
        SetBusy(true); CopyButton.IsEnabled = false; OutputBox.Clear();
        try
        {
            var result = await Task.Run(() =>
            {
                var bytes = file ?? ByteUtilities.Decode(input, source);
                var output = _checksum ? FormatChecksums(bytes) : ByteUtilities.Encode(bytes, destination);
                return (Output: output, Count: bytes.Length);
            });
            if (_closed) return;
            OutputBox.Text = result.Output; CopyButton.IsEnabled = result.Output.Length > 0;
            StatusText.Text = $"完成 · {result.Count:N0} 字节" + (_checksum ? " · CRC 数值按高位在前显示，LE 列为低字节在前。" : " · 文本不自动添加 BOM 或换行。");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        { if (!_closed) StatusText.Text = "输入无效：" + ex.Message; }
        finally { SetBusy(false); }
    }

    private static string FormatChecksums(byte[] bytes)
    {
        var text = new StringBuilder();
        foreach (var value in ByteUtilities.Checksums(bytes))
        {
            text.AppendLine(value.Algorithm).AppendLine($"  HEX: {value.Value}");
            if (value.LittleEndian != "—") text.AppendLine($"  LE:  {value.LittleEndian}");
            text.AppendLine($"  {value.Parameters}").AppendLine();
        }
        return text.ToString();
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var picker = new OpenFileDialog { Title = "读取原始文件字节", Filter = "所有文件|*.*" };
        if (picker.ShowDialog(this) != true) return;
        SetBusy(true);
        try
        {
            var limit = (_checksum ? 16 : 1) * 1024 * 1024;
            using var stream = new FileStream(picker.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (stream.Length > limit) throw new IOException($"此工具支持最多 {limit / 1024 / 1024} MiB 的文件。");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes);
            if (_closed) return;
            _fileBytes = bytes;
            InputBox.Text = $"{picker.FileName}\n\n{bytes.Length:N0} 字节 · 按文件原始字节计算，不经过文本解码。";
            InputBox.IsReadOnly = true; InputFormatBox.IsEnabled = false;
            ResetButton.Visibility = Visibility.Visible;
            OutputBox.Clear(); CopyButton.IsEnabled = false;
            StatusText.Text = "文件已载入，点击" + (_checksum ? "计算。" : "转换。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (!_closed) StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _fileBytes = null; InputBox.IsReadOnly = false; InputFormatBox.IsEnabled = true; InputBox.Clear();
        OutputBox.Clear(); CopyButton.IsEnabled = false; ResetButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "请输入要处理的数据。";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (_closed) return;
        ParametersPanel.IsEnabled = InputBox.IsEnabled = !busy;
        if (busy) StatusText.Text = "处理中…";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(OutputBox.Text); StatusText.Text = "已复制结果。"; }
        catch (System.Runtime.InteropServices.COMException) { StatusText.Text = "剪贴板正被占用，请重试。"; }
    }
}
