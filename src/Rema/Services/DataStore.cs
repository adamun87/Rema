using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Rema.Models;

namespace Rema.Services;

public class DataStore
{
    private static readonly string AppDir = ResolveAppDir();
    private static readonly string DataFile = Path.Combine(AppDir, "data.json");

    public static string ChatsDir { get; } = Path.Combine(AppDir, "chats");

    private RemaAppData _data;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _chatLoadLocksSync = new();
    private readonly Dictionary<Guid, SemaphoreSlim> _chatLoadLocks = new();

    public DataStore()
    {
        Directory.CreateDirectory(AppDir);
        Directory.CreateDirectory(ChatsDir);
        _data = Load();
        BuiltInCapabilityCatalog.EnsureBuiltIns(_data);
        NormalizeLoadedData(_data);
    }

    internal DataStore(RemaAppData data)
    {
        _data = data ?? new RemaAppData();
        BuiltInCapabilityCatalog.EnsureBuiltIns(_data);
        NormalizeLoadedData(_data);
    }

    public RemaAppData Data => _data;

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Create a backup of the current data file before overwriting
            if (File.Exists(DataFile))
            {
                try { File.Copy(DataFile, DataFile + ".bak", overwrite: true); }
                catch { /* Best-effort backup */ }
            }

            await WriteWithRetryAsync(
                DataFile,
                async stream => await JsonSerializer.SerializeAsync(
                    stream,
                    _data,
                    AppDataJsonContext.Default.RemaAppData,
                    cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SaveChatAsync(Chat chat, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(ChatsDir, $"{chat.Id}.json");
            await WriteWithRetryAsync(
                path,
                async stream => await JsonSerializer.SerializeAsync(
                    stream,
                    chat.Messages,
                    AppDataJsonContext.Default.ListChatMessage,
                    cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task LoadChatMessagesAsync(Chat chat, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(ChatsDir, $"{chat.Id}.json");
        if (!File.Exists(path))
            return;

        SemaphoreSlim loadLock;
        lock (_chatLoadLocksSync)
        {
            if (!_chatLoadLocks.TryGetValue(chat.Id, out loadLock!))
            {
                loadLock = new SemaphoreSlim(1, 1);
                _chatLoadLocks[chat.Id] = loadLock;
            }
        }

        await loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (chat.Messages.Count > 0)
                return;

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous);

            var messages = await JsonSerializer.DeserializeAsync(
                stream,
                AppDataJsonContext.Default.ListChatMessage,
                cancellationToken).ConfigureAwait(false);

            if (messages is not null)
                chat.Messages = messages;
        }
        catch (Exception)
        {
            // Corrupted chat file — skip gracefully
        }
        finally
        {
            loadLock.Release();
        }
    }

    private RemaAppData Load()
    {
        MigrateFromLegacyDir();

        if (!File.Exists(DataFile))
            return new RemaAppData();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var json = File.ReadAllText(DataFile);
                return JsonSerializer.Deserialize(json, AppDataJsonContext.Default.RemaAppData) ?? new RemaAppData();
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(35);
            }
            catch
            {
                // Try backup file
                var backupFile = DataFile + ".bak";
                if (File.Exists(backupFile))
                {
                    try
                    {
                        var json = File.ReadAllText(backupFile);
                        return JsonSerializer.Deserialize(json, AppDataJsonContext.Default.RemaAppData) ?? new RemaAppData();
                    }
                    catch { }
                }
                return new RemaAppData();
            }
        }

        return new RemaAppData();
    }

    /// <summary>
    /// One-time migration from the legacy %AppData%/Lumi directory.
    /// If the old directory has a newer data.json than the current one,
    /// copy data.json + chats/ + skills/ + browser-data/ over.
    /// </summary>
    private static void MigrateFromLegacyDir()
    {
        var legacyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumi");
        var legacyDataFile = Path.Combine(legacyDir, "data.json");

        if (!File.Exists(legacyDataFile))
            return;

        // Only migrate if the legacy data is newer or the current data.json doesn't exist.
        if (File.Exists(DataFile))
        {
            var legacyTime = File.GetLastWriteTimeUtc(legacyDataFile);
            var currentTime = File.GetLastWriteTimeUtc(DataFile);
            if (currentTime >= legacyTime)
                return;
        }

        try
        {
            // Copy data.json
            File.Copy(legacyDataFile, DataFile, overwrite: true);

            // Copy subdirectories that contain user data
            foreach (var subDir in new[] { "chats", "skills", "browser-data" })
            {
                var source = Path.Combine(legacyDir, subDir);
                if (!Directory.Exists(source))
                    continue;

                var target = Path.Combine(AppDir, subDir);
                Directory.CreateDirectory(target);
                foreach (var file in Directory.GetFiles(source))
                {
                    var dest = Path.Combine(target, Path.GetFileName(file));
                    if (!File.Exists(dest) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(dest))
                        File.Copy(file, dest, overwrite: true);
                }
            }
        }
        catch
        {
            // Migration is best-effort — don't crash the app if it fails.
        }
    }

    private static void NormalizeLoadedData(RemaAppData data)
    {
        foreach (var project in data.ServiceProjects)
            PipelineDefinitionIdResolver.Normalize(project);
    }

    private static async Task WriteWithRetryAsync(
        string path,
        Func<FileStream, Task> writeAction,
        CancellationToken ct,
        int maxRetries = 3,
        int retryDelayMs = 35)
    {
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous);

                await writeAction(stream).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < maxRetries - 1 && !ct.IsCancellationRequested)
            {
                await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
            }
        }
    }

    private static string ResolveAppDir()
    {
        var overrideDir = Environment.GetEnvironmentVariable("REMA_APPDATA_DIR");
        return string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rema")
            : overrideDir;
    }
}
