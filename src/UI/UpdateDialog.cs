using System.Diagnostics;
using LiteMonitor.src.Core;
using LiteMonitor.Updates;

namespace LiteMonitor;

public class DownloadContext
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Urls { get; set; } = [];
    public string SavePath { get; set; } = "";
    public string VersionLabel { get; set; } = "";
    public string ActionButtonText { get; set; } = "Install";
    public string? ReleasePageUrl { get; set; }
    public Version? UpdateVersion { get; set; }
}

public partial class UpdateDialog : Form
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly DownloadContext _context;
    private readonly bool _chinese;
    private CancellationTokenSource? _download;
    private bool _preparing;

    public UpdateDialog(DownloadContext context, Settings settings)
    {
        InitializeComponent();
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        _context = context;
        _chinese = settings.Language is "zh" or "zh-tw";

        Text = $"LiteMonitor - {context.Title}";
        lblVersion.Text = context.VersionLabel;
        label1.Text = context.Title;
        rtbChangelog.Text = context.Description;
        // Release notes are untrusted text. The release button uses the configured repository.
        rtbChangelog.DetectUrls = false;
        btnUpdate.Text = context.UpdateVersion != null
            ? T("下载并更新", "Download and update")
            : context.ReleasePageUrl != null ? T("打开发布页", "Open release page")
            : T("立即安装", context.ActionButtonText);
        btnCancel.Text = T("取消", "Cancel");
        lblStatus.Text = context.ReleasePageUrl != null && context.UpdateVersion == null
            ? T("此版本需手动安装", "This release requires manual installation")
            : T("准备就绪", "Ready");
        ActiveControl = btnUpdate;
    }

    private string T(string chinese, string english) => _chinese ? chinese : english;

    private void btnCancel_Click(object sender, EventArgs e)
    {
        if (_download != null) _download.Cancel();
        else Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = _preparing || _download != null;
            _download?.Cancel();
        }
        base.OnFormClosing(e);
    }

    private async void btnUpdate_Click(object sender, EventArgs e)
    {
        if (_context.ReleasePageUrl != null && _context.UpdateVersion == null)
        {
            try { Process.Start(new ProcessStartInfo(_context.ReleasePageUrl) { UseShellExecute = true }); }
            catch (Exception ex) { lblStatus.Text = ex.Message; }
            return;
        }

        if (_download != null || _preparing) return;
        using var cancel = new CancellationTokenSource();
        _download = cancel;
        btnUpdate.Enabled = false;
        btnCancel.Enabled = true;
        btnCancel.Text = T("取消", "Cancel");
        string? stage = null;
        string? partial = null;
        bool handedOff = false;

        try
        {
            string target = _context.SavePath;
            Version? version = _context.UpdateVersion;
            if (version != null)
            {
                stage = Path.Combine(AppContext.BaseDirectory, "resources", ".update-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                target = Path.Combine(stage, "package.zip");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            partial = target + "." + Guid.NewGuid().ToString("N") + ".part";
            var signature = await DownloadAsync(partial, version, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            File.Move(partial, target, overwrite: true);

            if (version == null)
            {
                _download = null;
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                _download = null;
                _preparing = true;
                btnCancel.Enabled = false;
                lblStatus.Text = T("正在准备更新…", "Preparing update…");
                File.WriteAllBytes(Path.Combine(stage!, "package.sig"), signature!);
                await StartUpdaterAsync(stage!, version);
                handedOff = true;
                Application.Exit();
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            lblStatus.Text = T("下载已取消", "Download canceled");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Update] {ex}");
            lblStatus.Text = T("更新或下载失败", "Update or download failed");
            if (!IsDisposed)
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _download = null;
            _preparing = false;
            if (partial != null) TryDelete(partial, directory: false);
            if (stage != null && !handedOff &&
                Path.GetDirectoryName(stage) == Path.Combine(AppContext.BaseDirectory, "resources"))
                TryDelete(stage, directory: true);

            if (!IsDisposed && !handedOff)
            {
                btnUpdate.Enabled = true;
                btnCancel.Enabled = true;
                btnCancel.Text = T("关闭", "Close");
                progress.Style = ProgressBarStyle.Blocks;
                progress.Value = 0;
            }
        }
    }

    private async Task<byte[]?> DownloadAsync(string partial, Version? version, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (string url in _context.Urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            idle.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                progress.Value = 0;
                lblStatus.Text = T("正在连接…", "Connecting…");
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token);
                response.EnsureSuccessStatusCode();
                long? expected = response.Content.Headers.ContentLength;
                if (expected is <= 0 or > UpdatePackage.MaxPackageBytes)
                    throw new InvalidDataException("Unexpected download size.");

                progress.Style = expected.HasValue ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                long received = 0;
                var clock = Stopwatch.StartNew();
                long nextRefresh = 0;
                var buffer = new byte[81920];
                await using (var input = await response.Content.ReadAsStreamAsync(idle.Token))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, useAsync: true))
                {
                    while (true)
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(20));
                        int read = await input.ReadAsync(buffer, idle.Token);
                        if (read == 0) break;
                        received += read;
                        if (received > UpdatePackage.MaxPackageBytes)
                            throw new InvalidDataException("Download limit exceeded.");
                        await output.WriteAsync(buffer.AsMemory(0, read), idle.Token);
                        if (clock.ElapsedMilliseconds < nextRefresh) continue;
                        nextRefresh = clock.ElapsedMilliseconds + 200;
                        if (expected.HasValue) progress.Value = (int)Math.Min(100, 100 * received / expected.Value);
                        lblStatus.Text = $"{received / 1048576d:F1} MB" +
                            (expected.HasValue ? $" / {expected.Value / 1048576d:F1} MB" : "");
                    }
                }

                if (received == 0 || (expected.HasValue && expected != received))
                    throw new InvalidDataException("The download is incomplete.");
                if (version == null) return null;

                lblStatus.Text = T("正在验证签名…", "Verifying signature…");
                idle.CancelAfter(TimeSpan.FromSeconds(20));
                using var signed = await Http.GetAsync(url + ".sig", HttpCompletionOption.ResponseHeadersRead, idle.Token);
                signed.EnsureSuccessStatusCode();
                await using var inputSignature = await signed.Content.ReadAsStreamAsync(idle.Token);
                var sig = new byte[UpdatePackage.MaxSignatureBytes + 1];
                int size = await inputSignature.ReadAtLeastAsync(sig, sig.Length, throwOnEndOfStream: false, idle.Token);
                if (size > UpdatePackage.MaxSignatureBytes)
                    throw new InvalidDataException("Unexpected signature size.");

                byte[] bytes = sig[..size];
                await Task.Run(() =>
                {
                    using var package = File.OpenRead(partial);
                    UpdatePackage.Verify(package, version, bytes);
                }, cancellationToken);
                return bytes;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException(T("下载连接超时", "The download connection timed out."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw lastError ?? new InvalidOperationException("No download source is available.");
    }

    private static async Task StartUpdaterAsync(string stage, Version version)
    {
        const string updaterName = "LiteMonitor.Updater.exe";
        string copiedUpdater = Path.Combine(stage, updaterName);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "resources", updaterName), copiedUpdater);

        string eventName = @"Local\LiteMonitorUpdate-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var startInfo = new ProcessStartInfo(copiedUpdater) { WorkingDirectory = stage };
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(version.ToString());
        startInfo.ArgumentList.Add(eventName);
        using var updater = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the updater.");
        bool accepted = await Task.Run(() =>
        {
            var timeout = Stopwatch.StartNew();
            while (!updater.HasExited && timeout.Elapsed < TimeSpan.FromMinutes(2))
            {
                if (ready.WaitOne(100)) return true;
            }
            return false;
        });
        if (!accepted)
            throw new InvalidOperationException("The updater could not prepare the installation. See update_error.log.");
    }

    private static void TryDelete(string path, bool directory)
    {
        try
        {
            if (directory)
            {
                if (Directory.Exists(path) &&
                    !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) &&
                    !File.GetAttributes(Path.GetDirectoryName(path)!).HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(path, recursive: true);
            }
            else File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        label1.Left = (ClientSize.Width - label1.Width) / 2;
        lblVersion.Left = (ClientSize.Width - lblVersion.Width) / 2;
    }
}
