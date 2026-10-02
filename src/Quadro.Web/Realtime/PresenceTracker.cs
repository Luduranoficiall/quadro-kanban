using System.Collections.Concurrent;

namespace Quadro.Web.Realtime;

/// <summary>Quem está com cada quadro aberto agora. Uma pessoa com duas abas aparece uma vez só.</summary>
public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _boards = new();

    public event Action<string, IReadOnlyList<string>>? Changed;

    public IReadOnlyList<string> Join(string boardId, string connectionId, string name)
    {
        _boards.GetOrAdd(boardId, _ => new())[connectionId] = name;
        return Notify(boardId);
    }

    public IReadOnlyList<string> Leave(string boardId, string connectionId)
    {
        if (_boards.TryGetValue(boardId, out var people)) people.TryRemove(connectionId, out _);
        return Notify(boardId);
    }

    public IReadOnlyList<string> Online(string boardId) =>
        _boards.TryGetValue(boardId, out var people)
            ? people.Values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    private IReadOnlyList<string> Notify(string boardId)
    {
        var online = Online(boardId);
        Changed?.Invoke(boardId, online);
        return online;
    }
}
