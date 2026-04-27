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

            await using var stream = new FileStream(
                DataFile,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous);

            await JsonSerializer.SerializeAsync(
                stream,
                _data,
                AppDataJsonContext.Default.RemaAppData,
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
            await using var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous);

            await JsonSerializer.SerializeAsync(
                stream,
                chat.Messages,
                AppDataJsonContext.Default.ListChatMessage,
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
        if (!File.Exists(DataFile))
            return new RemaAppData();

        try
        {
            var json = File.ReadAllText(DataFile);
            return JsonSerializer.Deserialize(json, AppDataJsonContext.Default.RemaAppData) ?? new RemaAppData();
        }
        catch
        {
            return new RemaAppData();
        }
    }

    private static void NormalizeLoadedData(RemaAppData data)
    {
        foreach (var project in data.ServiceProjects)
            PipelineDefinitionIdResolver.Normalize(project);
    }

    private static string ResolveAppDir()
    {
        var overrideDir = Environment.GetEnvironmentVariable("REMA_APPDATA_DIR");
        return string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rema")
            : overrideDir;
    }
}
