using System.Windows;
using System.Windows.Input;

namespace EmbeddedDebugger.App;

public partial class CidrCalculatorWindow : Window
{
    public CidrCalculatorWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Calculate();
    }

    private void Calculate_Click(object sender, RoutedEventArgs e) => Calculate();
    private void Input_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { Calculate(); e.Handled = true; } }

    private void Calculate()
    {
        try
        {
            var result = NetworkCidrCalculator.Calculate(InputBox.Text);
            FamilyText.Text = result.Family;
            PrefixText.Text = $"/{result.Prefix}";
            HostCountText.Text = result.HostCount;
            NetworkText.Text = result.Network;
            MaskText.Text = result.Mask;
            BroadcastText.Text = result.Broadcast;
            FirstText.Text = result.FirstHost;
            LastText.Text = result.LastHost;
        }
        catch (FormatException ex)
        {
            FamilyText.Text = PrefixText.Text = HostCountText.Text = NetworkText.Text = MaskText.Text = BroadcastText.Text = FirstText.Text = LastText.Text = ex.Message;
        }
    }
}
