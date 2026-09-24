namespace W_TB_MS
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // 崩溃现场抓取：任何未处理异常写入 exe 同目录 crash.log，便于定位启动即退/窗口消失问题。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                WriteCrashLog("ThreadException", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                WriteCrashLog("UnhandledException", e.ExceptionObject as Exception);

            ApplicationConfiguration.Initialize();
            // Application.Run 负责创建并显示主窗体，避免先 Show 再进入消息循环导致窗口不在前台。
            Application.Run(new MainForm());
        }

        private static void WriteCrashLog(string source, Exception? ex)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "crash.log");
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}: {ex}\r\n\r\n");
            }
            catch
            {
                // 日志写入失败时不再抛出，避免二次异常。
            }
        }
    }
}
