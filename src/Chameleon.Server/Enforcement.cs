using Chameleon.Core.Proxy;

namespace Chameleon.Server;

/// <summary>Снимок одного клиента для политик (аккаунт + трафик + лимит + живой трафик).</summary>
public sealed record ClientView(
    string Key,
    bool Enabled,
    long LimitBytes,
    long PeriodDown, // download за период (БД + живой)
    long LiveDown, // download активных сессий (уже включён в PeriodDown)
    bool Online);

/// <summary>Решение политики по клиенту.</summary>
public sealed record EnforcementAction(string Key, bool Kick, string Reason);

/// <summary>
/// Политика исполнения. Получает снимок клиентов, возвращает действия (кого закрыть).
/// Точка расширения: новые фичи (throttle, расписание, гео, ручной бан) - новые политики.
/// </summary>
public interface IEnforcementPolicy
{
    IEnumerable<EnforcementAction> Evaluate(IReadOnlyList<ClientView> clients);
}

/// <summary>Политика лимитов: клиент онлайн и download за период >= лимита -> закрыть.</summary>
public sealed class LimitPolicy : IEnforcementPolicy
{
    public IEnumerable<EnforcementAction> Evaluate(IReadOnlyList<ClientView> clients)
    {
        foreach (var c in clients)
            if (c is { Online: true, LimitBytes: > 0 } && c.PeriodDown >= c.LimitBytes)
                yield return new EnforcementAction(c.Key, Kick: true,
                    Reason: $"превышен лимит ({c.PeriodDown}/{c.LimitBytes} B за период)");
    }
}

/// <summary>
/// Фоновый исполнитель: раз в интервал строит снимок клиентов, прогоняет через все
/// политики и применяет действия (закрывает сессии). Добавить фичу = добавить политику.
/// </summary>
public sealed class EnforcementRunner : IDisposable
{
    private readonly ChameleonServer _server;
    private readonly SqliteStore _store;
    private readonly IReadOnlyList<IEnforcementPolicy> _policies;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();

    public EnforcementRunner(ChameleonServer server, SqliteStore store,
        IReadOnlyList<IEnforcementPolicy> policies, TimeSpan interval)
    {
        _server = server;
        _store = store;
        _policies = policies;
        _interval = interval;
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
                /* политика не должна валить сервер */
            }
        }
    }
    
    public void Tick()
    {
        var views = BuildViews();
        var seen = new HashSet<string>();
        foreach (var policy in _policies)
        foreach (var act in policy.Evaluate(views))
            if (act.Kick && seen.Add(act.Key))
                _server.CloseSessionsForClient(act.Key, act.Reason);
    }

    private IReadOnlyList<ClientView> BuildViews()
    {
        var live = new Dictionary<string, long>();
        foreach (var s in _server.ActiveSessions())
        {
            string k = s.ClientKey.ToLowerInvariant();
            live[k] = (live.TryGetValue(k, out var v) ? v : 0) + s.BytesToClient;
        }

        var list = new List<ClientView>();
        foreach (var r in _store.Rows())
        {
            long ld = live.TryGetValue(r.Key, out var d) ? d : 0;
            list.Add(new ClientView(r.Key, r.Enabled, r.LimitBytes, r.PeriodDown + ld, ld, live.ContainsKey(r.Key)));
        }

        return list;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}