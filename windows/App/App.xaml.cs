using System.IO;
using System.Windows;
using PhoneWebcam.Core.Diagnostics;

namespace PhoneWebcam.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneWebcam", "logs");
        Log.EnableFile(Path.Combine(logDir, "app.log"));
        Log.Info("Phone Webcam started");
        base.OnStartup(e);
    }
}
