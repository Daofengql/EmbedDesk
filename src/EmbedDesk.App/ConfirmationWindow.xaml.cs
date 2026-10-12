using System.Windows;

namespace EmbedDesk.App;

public partial class ConfirmationWindow : Window
{
    public ConfirmationWindow(string title, string message, string confirmLabel)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
