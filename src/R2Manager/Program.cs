using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CloudStore;

internal static class Program
{
    private static readonly Mutex SingleInstance = new(false, "CloudStore.SingleInstance.v1");

    [STAThread]
    private static void Main()
    {
        // 整个 Main 包一层：WinExe 里未处理的异常不会弹窗，进程直接消失，
        // 用户看到的就是「双击了但什么也没发生」。这里保证任何失败都有可见提示。
        try
        {
            Run();
        }
        catch (Exception ex)
        {
            EmergencyReport(ex, exit: true);
        }
    }

    private static void Run()
    {
        // 预览 GB18030 / GBK 等非 UTF-8 文本时需要
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point));
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) => EmergencyReport(e.Exception, exit: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => EmergencyReport(e.ExceptionObject as Exception, exit: true);

        bool created;
        try
        {
            created = SingleInstance.WaitOne(TimeSpan.Zero, false);
        }
        catch (AbandonedMutexException)
        {
            created = true;
        }

        if (!created)
        {
            try
            {
                MessageBox.Show("云存储管家已经在运行了。请在任务栏里找到它。", "云存储管家",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception)
            {
                // 忽略
            }

            return;
        }

        try
        {
            Application.Run(new UI.MainForm());
        }
        finally
        {
            try
            {
                SingleInstance.ReleaseMutex();
            }
            catch (Exception)
            {
                // 没拿到所有权时 ReleaseMutex 会抛，这里不需要它。
            }

            SingleInstance.Dispose();
        }
    }

    /// <summary>
    /// 最后一层兜底：把异常写进日志并弹窗。
    /// 这段代码刻意不依赖任何可能失败的东西（不读配置、不查目录、不调 SDK）。
    /// </summary>
    private static void EmergencyReport(Exception? ex, bool exit)
    {
        string detail = ex is null
            ? "发生了未知错误。"
            : $"{ex.GetType().FullName}\r\n\r\n{ex.Message}";

        string logPath = string.Empty;

        try
        {
            logPath = Core.ErrorInfo.AppendLog("未处理异常", ex ?? new Exception("未知错误"));
        }
        catch (Exception)
        {
            // 写不了日志就算了，下面至少要把错误摆到用户眼前。
        }

        try
        {
            string body = string.IsNullOrEmpty(logPath)
                ? detail
                : detail + "\r\n\r\n详细信息已写入：" + logPath;

            MessageBox.Show(body, "云存储管家启动失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            // 弹窗也失败时没有任何办法提示用户，绝不能在这里再抛出去。
        }

        try
        {
            if (exit)
            {
                Environment.Exit(1);
            }
        }
        catch (Exception)
        {
            // 忽略
        }
    }
}
