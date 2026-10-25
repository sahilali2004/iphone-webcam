using System.IO;
using System.Windows;
using IPhoneWebcam.Core.Diagnostics;

namespace IPhoneWebcam.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iPhoneWebcam", "logs");
        Log.EnableFile(Path.Combine(logDir, "app.log"));
        Log.Info("iPhone Webcam started");
        base.OnStartup(e);
    }
}
