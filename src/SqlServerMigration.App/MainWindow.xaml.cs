using System.Text;
using System.Windows;
using SqlServerMigration.App.Models;
using SqlServerMigration.App.Services;
using SqlServerMigration.App.ViewModels;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace SqlServerMigration.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
        : this(new MigrationUiOperations())
    {
    }

    public MainWindow(IMigrationUiOperations? operations)
    {
        InitializeComponent();
        _viewModel = new MainViewModel(operations);
        _viewModel.SetSecretProvider(ReadConnectionSecrets);
        DataContext = _viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        DataContext = null;
        _viewModel?.Dispose();
        _viewModel = null;
        SourcePasswordBox.Clear();
        TargetPasswordBox.Clear();
        base.OnClosed(e);
    }

    private MigrationConnectionSecrets ReadConnectionSecrets()
    {
        using var sourcePassword = SourcePasswordBox.SecurePassword;
        using var targetPassword = TargetPasswordBox.SecurePassword;
        return new MigrationConnectionSecrets(sourcePassword, targetPassword);
    }

    private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not PasswordBox passwordBox)
        {
            return;
        }

        MigrationEndpointRole role = string.Equals(passwordBox.Tag?.ToString(), "Target", StringComparison.Ordinal)
            ? MigrationEndpointRole.Target
            : MigrationEndpointRole.Source;
        using var currentPassword = passwordBox.SecurePassword;
        _viewModel.NotifySecretPresence(role, currentPassword.Length > 0);
    }
}
