using System.Collections.Concurrent;

namespace Chameleon.Core.Proxy;

/// <summary>Одно событие сервера (для лога и будущего API/веб-панели).</summary>
public sealed record ServerLogEntry(
    DateTime TimeUtc,
    string Event,
    string RemoteIp,
    string Detail);

/// <summary>
/// Журнал событий сервера: кольцевой буфер последних записей + событие Logged.
/// Хранит подключения/отключения/зонды. Читается будущим management-API.
/// </summary>
public sealed class ServerEventLog
{
    private readonly ConcurrentQueue<ServerLogEntry> _entries = new();
    private readonly int _max;

    public ServerEventLog(int maxEntries = 2000) => _max = maxEntries;

    /// <summary>Срабатывает при каждой записи (сервер Program подписывается и печатает в консоль).</summary>
    public event Action<ServerLogEntry>? Logged;

    public void Add(string @event, string remoteIp, string detail)
    {
        var entry = new ServerLogEntry(DateTime.UtcNow, @event, remoteIp, detail);
        _entries.Enqueue(entry);
        while (_entries.Count > _max && _entries.TryDequeue(out _))
        {
        }

        Logged?.Invoke(entry);
    }

    /// <summary>Последние N записей (свежие первыми) - для API/панели.</summary>
    public IReadOnlyList<ServerLogEntry> Recent(int count = 100)
        => [.. _entries.Reverse().Take(count)];
}

/// <summary>Снимок одной активной сессии (для мониторинга/API).</summary>
public sealed record SessionInfo(
    string SessionId,
    string RemoteIp,
    string ClientKey,
    DateTime StartedUtc,
    long UptimeSeconds,
    long BytesToClient,
    long BytesFromClient,
    int Carriers,
    int Streams);

/// <summary>Агрегированная статистика сервера.</summary>
public sealed record ServerStats(
    int ActiveSessions,
    long TotalBytesToClient,
    long TotalBytesFromClient,
    int TotalStreams);