namespace W_TB_jiankong
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            // Application.Run 负责创建并显示主窗体，避免先 Show 再进入消息循环导致窗口不在前台。
            Application.Run(new MainForm());
        }
    }
}
