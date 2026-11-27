using System.Windows;

namespace DBooyvol.App.Views;

public partial class PasswordDialog : Window
{
    public PasswordDialog(string profileName)
    {
        InitializeComponent();
        PromptText.Text = $"Пароль для «{profileName}»:";
        Loaded += (_, _) => PasswordInput.Focus();
    }

    public string Password => PasswordInput.Password;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
