using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WTbMsSetup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm());
    }
}

internal sealed class SetupForm : Form
{
    private static readonly string RuntimePayload = Environment.Is64BitOperatingSystem
        ? "win-x64"
        : "win-x86";
    private readonly TextBox _targetPath;
    private readonly Button _installButton;
    private readonly Label _status;

    internal SetupForm()
    {
        Text = "W_TB_MS v1.0.16 Setup";
        ClientSize = new Size(520, 190);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var title = new Label
        {
            Text = "W_TB_MS v1.0.16",
            Font = new Font("Microsoft YaHei", 14F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 20)
        };
        var prompt = new Label
        {
            Text = "选择安装目录：",
            AutoSize = true,
            Location = new Point(24, 68)
        };
        _targetPath = new TextBox
        {
            Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "W_TB_MS"),
            Location = new Point(24, 92),
            Width = 370
        };
        var browse = new Button { Text = "浏览...", Location = new Point(404, 90), Size = new Size(90, 28) };
        browse.Click += (_, _) => BrowseForFolder();
        _installButton = new Button { Text = "安装", Location = new Point(404, 135), Size = new Size(90, 28) };
        _installButton.Click += async (_, _) => await InstallAsync();
        _status = new Label { AutoSize = true, Location = new Point(24, 140), ForeColor = Color.Gray };

        Controls.AddRange(new Control[] { title, prompt, _targetPath, browse, _installButton, _status });
    }

    private void BrowseForFolder()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _targetPath.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _targetPath.Text = dialog.SelectedPath;
    }

    private async Task InstallAsync()
    {
        string target = Path.GetFullPath(_targetPath.Text.Trim());
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, "请选择安装目录。", "W_TB_MS v1.0.16 Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _installButton.Enabled = false;
        _status.Text = "正在安装，请稍候...";
        try
        {
                string tempZip = Path.Combine(Path.GetTempPath(), $"W_TB_MS-v1.0.16-{RuntimePayload}-{Guid.NewGuid():N}.zip");
            try
            {
                await using (Stream source = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream($"WTbMsSetup.payload-{RuntimePayload}.zip")
                    ?? throw new InvalidDataException($"安装包缺少 {RuntimePayload} 程序文件。"))
                await using (FileStream destination = File.Create(tempZip))
                    await source.CopyToAsync(destination);

                await StopRunningApplicationAsync();
                Directory.CreateDirectory(target);
                await ExtractWithRetryAsync(tempZip, target);
            }
            finally
            {
                try { File.Delete(tempZip); } catch { }
            }

            string executable = Path.Combine(target, "W_TB_MS.exe");
            if (!File.Exists(executable))
                throw new InvalidDataException("安装完成但未找到 W_TB_MS.exe。安装包可能已损坏。");

            string shortcutStatus = "";
            try
            {
                CreateDesktopShortcut(executable);
                shortcutStatus = "桌面快捷方式已创建。";
            }
            catch (Exception shortcutException)
            {
                // 快捷方式失败不应回滚已经完成的程序安装。
                shortcutStatus = $"桌面快捷方式创建失败：{shortcutException.Message}";
            }

            _status.Text = $"安装完成。{shortcutStatus}";
            string completionMessage = $"W_TB_MS v1.0.16 安装完成。\n{shortcutStatus}\n\n是否立即启动？";
            if (MessageBox.Show(this, completionMessage, "安装完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = "安装失败。";
            _installButton.Enabled = true;
            MessageBox.Show(this, $"安装失败：{ex.Message}", "W_TB_MS v1.0.16 Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static async Task StopRunningApplicationAsync()
    {
        Process[] processes = Process.GetProcessesByName("W_TB_MS");
        foreach (Process process in processes)
        {
            try
            {
                if (process.Id != Environment.ProcessId && !process.HasExited)
                    process.CloseMainWindow();
            }
            catch
            {
                // The process may exit between enumeration and CloseMainWindow.
            }
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            bool anyRunning = false;
            foreach (Process process in processes)
            {
                try
                {
                    process.Refresh();
                    if (!process.HasExited)
                    {
                        anyRunning = true;
                        break;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process already exited.
                }
            }

            if (!anyRunning)
                return;
            await Task.Delay(200);
        }

        throw new IOException("检测到 W_TB_MS 仍在运行，请先关闭软件后再安装。");
    }

    private static async Task ExtractWithRetryAsync(string tempZip, string target)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, target, true));
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == maxAttempts)
                    throw new IOException("安装文件仍被其他程序占用，请关闭 W_TB_MS 后重试。", ex);
                await Task.Delay(500);
            }
        }
    }

    private static void CreateDesktopShortcut(string executable)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
            throw new InvalidOperationException("无法确定当前用户桌面路径。");

        string shortcutPath = Path.Combine(desktop, "W_TB_MS v1.0.16.lnk");
        Type shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)
            ?? throw new InvalidOperationException("系统未提供快捷方式创建组件。");
        object shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法创建快捷方式组件。");
        object? shortcut = null;
        try
        {
            const BindingFlags invokeFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.InvokeMethod;
            shortcut = shellType.InvokeMember("CreateShortcut", invokeFlags, null, shell, new object[] { shortcutPath });
            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.SetProperty, null, shortcut, new object[] { executable });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(executable)! });
            shortcutType.InvokeMember("Description", BindingFlags.Instance | BindingFlags.Public | BindingFlags.SetProperty, null, shortcut, new object[] { "W_TB_MS v1.0.16" });
            shortcutType.InvokeMember("IconLocation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.SetProperty, null, shortcut, new object[] { $"{executable},0" });
            shortcutType.InvokeMember("Save", invokeFlags, null, shortcut, Array.Empty<object>());
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
                Marshal.FinalReleaseComObject(shortcut);
            if (Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }
    }
}
