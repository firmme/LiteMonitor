using System.Collections.Concurrent;
using System.Text.Json;

namespace LiteMonitor.src.Plugins;

internal sealed class PluginRequestCache
{
    internal sealed record Key(string Instance, string Target, string Step, string Url, string Body,
        string Method, string Proxy, string Encoding, string Headers)
    {
        public static Key Create(string instance, string target, string step, string url, string body,
            string? method, string? proxy, string? encoding, Dictionary<string, string>? headers)
        {
            string headerIdentity = headers == null || headers.Count == 0 ? "" : JsonSerializer.Serialize(headers
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new[] { pair.Key.ToUpperInvariant(), pair.Value }));
            return new Key(instance, target, step, url, body, (method ?? "GET").ToUpperInvariant(),
                proxy?.Trim() ?? "", encoding?.ToUpperInvariant() ?? "", headerIdentity);
        }
    }

    private readonly ConcurrentDictionary<Key, Lazy<Task<string>>> _active = new();
    private readonly ConcurrentDictionary<Key, (string Value, long Added)> _completed = new();

    public bool TryRead(Key key, int minutes, out string value)
    {
        if (minutes > 0 && _completed.TryGetValue(key, out var cached) &&
            Environment.TickCount64 - cached.Added < TimeSpan.FromMinutes(minutes).TotalMilliseconds)
        {
            value = cached.Value;
            return true;
        }

        value = "";
        return false;
    }

    public void Store(Key key, string value, int minutes)
    {
        if (minutes <= 0 || value.Length > 500 * 1024) return;
        if (_completed.Count >= 100)
        {
            foreach (var entry in _completed.OrderBy(pair => pair.Value.Added).Take(20))
                _completed.TryRemove(entry);
        }
        _completed[key] = (value, Environment.TickCount64);
    }

    public Task<string> FetchAsync(Key key, Func<Task<string>> fetch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = _active.GetOrAdd(key, _ => new Lazy<Task<string>>(RunAsync));
        return operation.Value.WaitAsync(cancellationToken);

        async Task<string> RunAsync()
        {
            try { return await fetch().ConfigureAwait(false); }
            finally { _active.TryRemove(key, out _); }
        }
    }

    public void Clear(string? instance = null)
    {
        if (string.IsNullOrEmpty(instance)) _completed.Clear();
        else
        {
            foreach (var key in _completed.Keys)
                if (key.Instance == instance) _completed.TryRemove(key, out _);
        }
    }
}
