using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
namespace HardwareLens.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            if (desktop.Args is ["--screenshot", var output])
            {
                window.Opened += async (_, _) =>
                {
                    await Task.Delay(6000);
                    window.Navigate("Performance");
                    window.UpdateLayout();
                    await Task.Delay(500);
                    var path = System.IO.Path.GetFullPath(output);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                    using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                        new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
                    bitmap.Render(window);
                    bitmap.Save(path);
                    window.Close();
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
