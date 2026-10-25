using System.ComponentModel;
using System.Windows;
using PhoneWebcam.App.ViewModels;

namespace PhoneWebcam.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = (MainViewModel)DataContext;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ShowPairing) && vm.ShowPairing)
                Dispatcher.BeginInvoke(() => PairingCodeBox.Focus());
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ((MainViewModel)DataContext).Dispose();
        base.OnClosing(e);
    }
}
