namespace W_TB_jiankong
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            var form = new MainForm();
            form.Show();
            Application.Run(form);
        }
    }
}
