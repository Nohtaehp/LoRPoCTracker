using System.Windows;
using LoRPoCTracker.Services;

namespace LoRPoCTracker.Views;

/// <summary>退出确认：可勾选「下次不再提示」。</summary>
public partial class ExitConfirmWindow : Window
{
    public ExitConfirmWindow()
    {
        InitializeComponent();
        ThemeManager.RegisterWindow(this);
    }

    /// <summary>用户是否勾选了「下次不再提示」。</summary>
    public bool DontAskAgain => DontAskBox.IsChecked == true;

    private void Exit_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
