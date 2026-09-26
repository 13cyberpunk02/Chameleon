namespace Chameleon.Core.Proxy;

/// <summary>Хранилище разрешённых клиентов (allowlist). Реализуется JSON'ом или SQLite.</summary>
public interface IClientStore
{
    bool Enforced { get; }
    int Count { get; }
    bool IsAllowed(string publicKeyHex);
    IReadOnlyList<ClientAccount> List();
    void Add(ClientAccount account);
    bool Remove(string publicKeyHex);
    bool SetEnabled(string publicKeyHex, bool enabled);
}