using System.Windows;

namespace Runner;

internal sealed class StartupDataException(Exception inner) : Exception("数据读取失败，软件不会覆盖该文件：" + inner.Message, inner);

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var window = new MainWindow();
            if (!window.StartupAborted) { MainWindow = window; window.Show(); }
        }
        catch (StartupDataException ex)
        {
            System.Windows.MessageBox.Show(ex.Message);
            Shutdown(1);
        }
    }
}
