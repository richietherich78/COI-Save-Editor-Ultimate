using System.Runtime.CompilerServices;
using System.Windows;
using Application = System.Windows.Application;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBox = System.Windows.MessageBox;

[assembly: InternalsVisibleTo("COISaveEditor-Ultimate.Tests")]

namespace COISaveEditorUltimate;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Surface any unhandled exceptions with a dialog instead of silent crash.
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.ToString(), "Unhandled error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
    }
}
