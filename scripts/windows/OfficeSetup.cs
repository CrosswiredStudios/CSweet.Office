using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// The signed EXE embeds the immutable release URLs, lengths and SHA-256 digests.
// Keep verified files locked through Windows Installer's final read of the cabinets.
internal sealed class OfficeSetup : Form
{
#if WINDOWS_TEST_INSTALLER
    const string ReleasePrefix = "windows-test-";
    const string MsiOptions = " CSWEET_SETUP_EXE=1";
#else
    const string ReleasePrefix = "v";
    const string MsiOptions = "";
#endif
    const string ShortcutName = "C-Sweet Office";
    const string WindowTitle = "C-Sweet Office Setup";
    readonly Label message = new Label { AutoSize = false, Dock = DockStyle.Fill, Text = "Download and install C-Sweet Office on this computer." };
    readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Bottom };
    readonly Button install = new Button { Text = "Install Office", Dock = DockStyle.Bottom, Height = 40 };
    readonly CancellationTokenSource cancel = new CancellationTokenSource();
    bool running, installing;

    OfficeSetup()
    {
        Text = WindowTitle; ClientSize = new Size(500, 155); Padding = new Padding(20);
#if WINDOWS_TEST_INSTALLER
        install.Text = "Continue";
        message.Text = "Continue to download and verify C-Sweet Office Setup. The setup wizard will show what this PC needs before you start the installation.";
#endif
        Font = SystemFonts.MessageBoxFont; StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(message); Controls.Add(progress); Controls.Add(install);
        install.Click += async delegate { await Run(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (running) { e.Cancel = true; if (!installing) cancel.Cancel(); }
        };
    }

    [STAThread] public static void Main()
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Application.EnableVisualStyles(); Application.Run(new OfficeSetup());
    }

    async Task Run()
    {
        if (running) return;
        running = true; install.Enabled = false;
        var held = new List<FileStream>();
        try
        {
            using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("OfficeFiles")))
            using (var http = new HttpClient { Timeout = TimeSpan.FromHours(4) })
            {
                var version = reader.ReadLine();
                var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSweet", "Downloads", version);
                Directory.CreateDirectory(cache);
                string line, msi = null;
                while ((line = reader.ReadLine()) != null)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    var parts = line.Split('|');
                    var path = Path.Combine(cache, parts[0]); var length = long.Parse(parts[1]);
                    var handle = await Verify(path, length, parts[2]);
                    if (handle == null)
                    {
                        message.Text = "Downloading " + parts[0]; progress.Value = 0;
                        var temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
                        try
                        {
                            var url = "https://github.com/CrosswiredStudios/CSweet.Office/releases/download/" + ReleasePrefix + version + "/" + parts[0];
                            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel.Token))
                            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                            {
                                response.EnsureSuccessStatusCode();
                                using (var input = await response.Content.ReadAsStreamAsync())
                                {
                                    var buffer = new byte[131072]; long total = 0; int count;
                                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancel.Token)) != 0)
                                    {
                                        total += count; if (total > length) throw new InvalidDataException();
                                        await file.WriteAsync(buffer, 0, count, cancel.Token);
                                        progress.Value = (int)(100L * total / length);
                                    }
                                }
                            }
                            if (File.Exists(path)) File.Delete(path);
                            File.Move(temp, path);
                        }
                        finally { if (File.Exists(temp)) File.Delete(temp); }
                        handle = await Verify(path, length, parts[2]);
                        if (handle == null) throw new InvalidDataException("The downloaded package failed verification.");
                    }
                    held.Add(handle);
                    if (Path.GetExtension(path).Equals(".msi", StringComparison.OrdinalIgnoreCase)) msi = path;
                }
                if (msi == null) throw new InvalidDataException("Missing Windows installer.");
                installing = true; message.Text = "Follow the Windows installer. Keep this window open.";
                using (var proc = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), "/i \"" + msi + "\"" + MsiOptions) { UseShellExecute = true }))
                {
                    await Task.Run(() => proc.WaitForExit());
                    if (proc.ExitCode != 0 && proc.ExitCode != 3010) throw new InvalidOperationException("Windows Installer returned " + proc.ExitCode + ".");
                    message.Text = (proc.ExitCode == 3010 ? "Restart Windows, then open " : "Installed. Open ") + ShortcutName + " from Start.";
                    var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ShortcutName + ".lnk");
                    if (proc.ExitCode == 0 && File.Exists(shortcut))
                    {
                        Process.Start(shortcut);
#if WINDOWS_TEST_INSTALLER
                        installing = false; running = false; Close();
#endif
                    }
                }
            }
            install.Visible = false;
        }
        catch (OperationCanceledException) { message.Text = "Download cancelled."; }
        catch (Exception e) { message.Text = e.Message + " Reopen setup to retry. Verified downloads are saved."; }
        finally { foreach (var stream in held) stream.Dispose(); installing = false; running = false; if (cancel.IsCancellationRequested) Close(); }
    }

    static Task<FileStream> Verify(string path, long length, string hash)
    {
        return Task.Run(() => {
            if (!File.Exists(path)) return null;
            var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                using (var sha = SHA256.Create())
                    if (file.Length == length && BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").Equals(hash, StringComparison.OrdinalIgnoreCase)) return file;
                file.Dispose(); return null;
            }
            catch { file.Dispose(); throw; }
        });
    }
}
