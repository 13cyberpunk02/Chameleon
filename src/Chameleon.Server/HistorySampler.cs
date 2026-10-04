using System.Collections.Concurrent;
using Chameleon.Core.Proxy;

namespace Chameleon.Server;

/// <summary>
/// Пишет ЖИВУЮ историю трафика по дням, дельтами по сессиям - чтобы график рос в
/// реальном времени, а не только после закрытия сессии. Без двойного счёта:
///   counted[sessionId] = сколько уже записано в историю;
///   тик: для каждой активной сессии добавляем прирост (current - counted);
///   закрытие: добавляем остаток (final - counted) и забываем сессию.
/// Сумма записанного == полный трафик сессии.
///
/// Изолирован от учёта/лимитов (те пишутся при закрытии в traffic). Это отдельный
/// слой истории - легко расширить (часовая гранулярность, иные метрики).
/// </summary>
public sealed class HistorySampler : IDisposable
{
    private readonly ChameleonServer _server;
    private readonly SqliteStore _store;
    private readonly TimeSpan _interval;

    private readonly CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<string, (long down, long up)> _counted = new();

    public HistorySampler(ChameleonServer server, SqliteStore store, TimeSpan interval)
    {
        _server = server;
        _store = store;
        _interval = interval;
        _server.SessionClosed += OnSessionClosed;
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, _cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            try
            {
                Tick();
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>Один проход: прирост активных сессий → в историю. public для тестируемости.</summary>
    public void Tick()
    {
        foreach (var s in _server.ActiveSessions())
        {
            string sid = s.SessionId;
            var prev = _counted.TryGetValue(sid, out var p) ? p : (0L, 0L);
            long dd = s.BytesToClient - prev.Item1;
            long du = s.BytesFromClient - prev.Item2;
            if (dd > 0 || du > 0)
            {
                _store.AddHistory(s.ClientKey, Math.Max(0, dd), Math.Max(0, du));
                _counted[sid] = (s.BytesToClient, s.BytesFromClient);
            }
        }
    }

    private void OnSessionClosed(string sessionId, string clientKey, long down, long up)
    {
        var prev = _counted.TryGetValue(sessionId, out var p) ? p : (0L, 0L);
        long dd = down - prev.Item1;
        long du = up - prev.Item2;
        if (dd > 0 || du > 0) _store.AddHistory(clientKey, Math.Max(0, dd), Math.Max(0, du));
        _counted.TryRemove(sessionId, out _);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _server.SessionClosed -= OnSessionClosed;
        _cts.Dispose();
    }
}