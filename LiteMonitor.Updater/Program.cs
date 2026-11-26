using System.Diagnostics;
using System.IO.Compression;
using LiteMonitor.Updates;

namespace LiteMonitor.Updater;

internal static class Program
{
    private const string MainExe = "LiteMonitor.exe";
    private const string UpdaterExe = "LiteMonitor.Updater.exe";
    private const string UpdatePrefix = ".update-";
    private const long MaxExtractedBytes = 1024L * 1024 * 1024;

    private static int Main(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[0], out int pid) ||
            !Version.TryParse(args[1], out var version))
            return 1;

        string stage = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var resources = Directory.GetParent(stage);
        var install = resources?.Parent;
        if (install == null || !resources!.Name.Equals("resources", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(stage).StartsWith(UpdatePrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(stage)[UpdatePrefix.Length..], "N", out _))
            return 1;

        string root = install.FullName;
        string executable = Path.Combine(root, MainExe);
        bool originalExited = false;
        bool canRestart = true;
        string stageName = Path.GetFileName(stage);

        try
        {
            RejectReparsePoints(root, stage);
            using var parent = Process.GetProcessById(pid);
            if (!string.Equals(parent.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The updating process is not from this installation.");

            if (ReadVersion(executable) >= AsFileVersion(version))
                throw new InvalidOperationException("The update version must be newer than the installed version.");

            string zipPath = Path.Combine(stage, "package.zip");
            string sigPath = Path.Combine(stage, "package.sig");
            RejectReparsePoints(root, zipPath);
            RejectReparsePoints(root, sigPath);
            string files;
            using (var package = File.Open(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using var signature = File.Open(sigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (signature.Length is <= 0 or > UpdatePackage.MaxSignatureBytes)
                    throw new InvalidDataException("Invalid signature size.");
                byte[] bytes = new byte[(int)signature.Length];
                signature.ReadExactly(bytes);
                UpdatePackage.Verify(package, version, bytes);
                files = Extract(package, Path.Combine(stage, "files"), version);
            }

            using (var ready = EventWaitHandle.OpenExisting(args[2]))
                ready.Set();
            if (!parent.WaitForExit(30_000))
                throw new TimeoutException("LiteMonitor did not exit in time.");
            originalExited = true;

            InstallFiles(root, stage, files, executable, out canRestart);
            return 0;
        }
        catch (Exception ex)
        {
            WriteError(root, ex);
            if (originalExited && canRestart)
            {
                try { WriteMarker(root, "update_failed", stageName); }
                catch (Exception markerError) { WriteError(root, markerError); }
                try { StartMain(executable, root); }
                catch (Exception restartError) { WriteError(root, restartError); }
            }
            return 1;
        }
    }

    private static string Extract(Stream package, string destination, Version version)
    {
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        long size = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length < 0 || entry.Length > MaxExtractedBytes - size)
                throw new InvalidDataException("The unpacked update is too large.");
            size += entry.Length;
        }

        Directory.CreateDirectory(destination);
        archive.ExtractToDirectory(destination);

        var children = Directory.GetFileSystemEntries(destination);
        string files = children.Length == 1 && Directory.Exists(children[0]) ? children[0] : destination;
        if (ReadVersion(Path.Combine(files, MainExe)) != AsFileVersion(version) ||
            !File.Exists(Path.Combine(files, "resources", UpdaterExe)))
            throw new InvalidDataException("The update is incomplete or has an unexpected version.");
        return files;
    }

    private static void InstallFiles(string root, string stage, string files, string executable, out bool rollbackSucceeded)
    {
        rollbackSucceeded = true;
        string backup = Path.Combine(stage, "backup");
        var applied = new List<(string Destination, string? Backup)>();
        string? marker = null;

        try
        {
            foreach (string source in Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(files, source);
                string destination = Path.Combine(root, relative);
                if (relative.StartsWith("resources\\" + UpdatePrefix, StringComparison.OrdinalIgnoreCase) ||
                    relative.ToLowerInvariant() is "update_success" or "update_failed" or "update_error.log" or
                        "settings.json" or "traffic.json" or "hardwarehistory.json")
                    throw new InvalidDataException("Unexpected update file path.");

                RejectReparsePoints(root, destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    string oldPath = Path.Combine(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
                    File.Move(destination, oldPath);
                    applied.Add((destination, oldPath));
                    File.Move(source, destination);
                }
                else
                {
                    File.Move(source, destination);
                    applied.Add((destination, null));
                }
            }
            marker = WriteMarker(root, "update_success", Path.GetFileName(stage));
            StartMain(executable, root);
        }
        catch (Exception ex)
        {
            if (marker != null)
            {
                try { File.Delete(marker); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            var errors = new List<Exception> { ex };
            for (int i = applied.Count - 1; i >= 0; i--)
            {
                var (destination, oldPath) = applied[i];
                try
                {
                    if (oldPath == null) File.Delete(destination);
                    else File.Move(oldPath, destination, overwrite: true);
                }
                catch (Exception rollbackError) { errors.Add(rollbackError); }
            }

            rollbackSucceeded = errors.Count == 1;
            if (!rollbackSucceeded)
                throw new AggregateException($"Rollback incomplete. Backups are in {backup}.", errors);
            throw;
        }
    }

    private static void RejectReparsePoints(string root, string path)
    {
        string current = root;
        foreach (string component in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"Update cannot follow a reparse point: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static Version ReadVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    private static Version AsFileVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private static void StartMain(string executable, string root)
    {
        using var process = Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = root });
        if (process == null) throw new InvalidOperationException("Could not restart LiteMonitor.");
    }

    private static string WriteMarker(string root, string name, string stage)
    {
        string path = Path.Combine(root, name);
        RejectReparsePoints(root, path);
        File.WriteAllText(path, stage);
        return path;
    }

    private static void WriteError(string root, Exception error)
    {
        try
        {
            string path = Path.Combine(root, "update_error.log");
            RejectReparsePoints(root, path);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {error}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
