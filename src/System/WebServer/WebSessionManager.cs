using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace LiteMonitor.src.WebServer;

public sealed class WebSessionManager : IDisposable
{
    private const int MaxConnections = 32;
    private const int MaxHeaderBytes = 8192;
    private const int MaxPasswordBytes = 1024;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);

    private readonly Action<Utf8JsonWriter> _dataProvider;
    private readonly X509Certificate2? _certificate;
    private readonly string? _networkHost;
    private readonly string _language;
    private readonly byte[]? _passwordHash;
    private bool _disposed;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _snapshotGate = new();
    private readonly object _sessionGate = new();
    private readonly Dictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private DateTimeOffset _loginWindow;
    private int _loginAttempts;
    private byte[] _snapshot = [];
    private long _snapshotTime = long.MinValue;
    private Task? _run;

    public WebSessionManager(Action<Utf8JsonWriter> dataProvider, X509Certificate2? certificate = null,
        string? networkHost = null, string? password = null, string? language = null)
    {
        _dataProvider = dataProvider;
        _certificate = certificate;
        _networkHost = networkHost;
        _language = language switch { "zh" => "zh-CN", "zh-tw" => "zh-TW", _ => "en" };
        _passwordHash = password == null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(password));
    }

    public void Start(TcpListener listener) => _run = Task.Run(() => RunAsync(listener, _stopping.Token));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping.Cancel();
        try { _run?.GetAwaiter().GetResult(); }
        finally
        {
            _stopping.Dispose();
            _certificate?.Dispose();
        }
    }

    private async Task RunAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        var active = new List<Task>(MaxConnections);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                active.RemoveAll(task => task.IsCompleted);
                if (active.Count == MaxConnections) client.Dispose();
                else active.Add(HandleClientAsync(client, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException ex) { Debug.WriteLine($"[Web listener] {ex.Message}"); }
        finally
        {
            listener.Stop();
            await Task.WhenAll(active);
        }
    }

    public async Task HandleClientAsync(TcpClient client, CancellationToken stopping = default)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            try
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var transport = client.GetStream();
                using var tls = _certificate == null ? null : new SslStream(transport, leaveInnerStreamOpen: true);
                if (tls != null)
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate
                    }, deadline.Token);
                Stream stream = (Stream?)tls ?? transport;

                var request = await ReadRequestAsync(stream, deadline.Token);
                if (request == null) return;
                int port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
                var origin = RequestOrigin(request, port);
                if (origin == null)
                {
                    await RespondAsync(stream, "403 Forbidden", [], deadline.Token);
                    return;
                }

                await RouteAsync(stream, request, origin, deadline.Token, stopping);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                or System.Security.Authentication.AuthenticationException or WebSocketException or ObjectDisposedException)
            {
                Debug.WriteLine($"[Web connection] {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Web request] {ex}");
            }
        }
    }

    private string? RequestOrigin(Request request, int port)
    {
        string scheme = _certificate == null ? "http" : "https";
        string authority = request.Header("Host");
        if (authority.Length == 0 || authority.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or ':')))
            return null;
        if (!Uri.TryCreate($"{scheme}://{authority}", UriKind.Absolute, out var url))
            return null;
        bool allowed = _networkHost == null
            ? url.Host is "127.0.0.1" or "localhost"
            : string.Equals(url.Host, _networkHost, StringComparison.OrdinalIgnoreCase);
        if (!allowed || (url.Port != port && authority.Contains(':')))
            return null;

        string origin = url.GetLeftPart(UriPartial.Authority);
        string suppliedOrigin = request.Header("Origin");
        if (suppliedOrigin.Length != 0 && !string.Equals(suppliedOrigin, origin, StringComparison.OrdinalIgnoreCase))
            return null;

        return origin;
    }

    private async Task RouteAsync(Stream stream, Request request, string origin,
        CancellationToken deadline, CancellationToken stopping)
    {
        if (request is { Method: "GET", Path: "/" })
        {
            string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            string ws = origin.StartsWith("https:", StringComparison.Ordinal) ? "wss" + origin[5..] : "ws" + origin[4..];
            string html = WebPageContent.IndexHtml
                .Replace("{{FAVICON}}", $"<link rel='icon' type='image/png' href='data:image/png;base64,{WebPageContent.GetAppIconBase64()}'>")
                .Replace("{{NONCE}}", nonce)
                .Replace("{{LANG}}", _language);
            string csp = $"Content-Security-Policy: default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; img-src data:; connect-src 'self' {ws}; frame-ancestors 'none'; form-action 'self'; base-uri 'none'\r\n";
            await RespondAsync(stream, "200 OK", Encoding.UTF8.GetBytes(html), deadline, "text/html; charset=utf-8", csp);
            return;
        }

        if (request is { Method: "POST", Path: "/api/session" })
        {
            await SignInAsync(stream, request, deadline);
            return;
        }

        if (request is { Method: "GET", Path: "/api/session" or "/api/snapshot" or "/ws" })
        {
            DateTimeOffset? expires = SessionExpiry(request);
            if (expires == null)
            {
                await RespondAsync(stream, "401 Unauthorized", [], deadline);
                return;
            }

            if (request.Path == "/ws")
                await OpenWebSocketAsync(stream, request, expires.Value, deadline, stopping);
            else if (request.Path == "/api/snapshot")
                await RespondAsync(stream, "200 OK", Snapshot(), deadline, "application/json");
            else
                await RespondAsync(stream, "204 No Content", [], deadline);
            return;
        }

        await RespondAsync(stream, "404 Not Found", [], deadline);
    }

    private async Task SignInAsync(Stream stream, Request request, CancellationToken deadline)
    {
        if (_passwordHash == null || request.Header("Origin").Length == 0 || request.Body.Length == 0 ||
            !request.Header("Content-Type").Split(';', 2)[0].Trim().Equals("text/plain", StringComparison.OrdinalIgnoreCase))
        {
            await RespondAsync(stream, "403 Forbidden", [], deadline);
            return;
        }

        bool allowed;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_sessionGate)
        {
            if (now - _loginWindow >= TimeSpan.FromMinutes(1))
            {
                _loginWindow = now;
                _loginAttempts = 0;
            }
            allowed = ++_loginAttempts <= 20;
        }

        if (!allowed || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(request.Body), _passwordHash))
        {
            await RespondAsync(stream, allowed ? "401 Unauthorized" : "429 Too Many Requests", [], deadline);
            return;
        }

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        bool hasCapacity;
        lock (_sessionGate)
        {
            foreach (var expired in _sessions.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                _sessions.Remove(expired);
            hasCapacity = _sessions.Count < 64;
            if (hasCapacity) _sessions.Add(token, now + SessionLifetime);
        }
        if (!hasCapacity)
        {
            await RespondAsync(stream, "429 Too Many Requests", [], deadline);
            return;
        }

        await RespondAsync(stream, "204 No Content", [], deadline, headers:
            $"Set-Cookie: __Host-LiteMonitor={token}; Max-Age=1800; Path=/; Secure; HttpOnly; SameSite=Strict\r\n");
    }

    private DateTimeOffset? SessionExpiry(Request request)
    {
        if (_passwordHash == null) return DateTimeOffset.MaxValue;

        foreach (string entry in request.Header("Cookie").Split(';', StringSplitOptions.TrimEntries))
        {
            const string name = "__Host-LiteMonitor=";
            if (!entry.StartsWith(name, StringComparison.Ordinal)) continue;
            lock (_sessionGate)
                return _sessions.TryGetValue(entry[name.Length..], out var expiry) && expiry > DateTimeOffset.UtcNow
                    ? expiry : null;
        }

        return null;
    }

    private async Task OpenWebSocketAsync(Stream stream, Request request, DateTimeOffset expires,
        CancellationToken handshake, CancellationToken stopping)
    {
        string key = request.Header("Sec-WebSocket-Key");
        byte[] decoded = new byte[16];
        if (!request.Header("Upgrade").Equals("websocket", StringComparison.OrdinalIgnoreCase) ||
            !request.Header("Connection").Split(',').Any(value => value.Trim().Equals("upgrade", StringComparison.OrdinalIgnoreCase)) ||
            request.Header("Sec-WebSocket-Version") != "13" || request.Header("Origin").Length == 0 ||
            !Convert.TryFromBase64String(key, decoded, out int size) || size != decoded.Length)
        {
            await RespondAsync(stream, "400 Bad Request", [], handshake);
            return;
        }

        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        string response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), handshake);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        if (expires != DateTimeOffset.MaxValue)
        {
            var remaining = expires - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            lifetime.CancelAfter(remaining);
        }
        using var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(20));
        var receiver = ReceiveAsync(socket, lifetime);
        try
        {
            using var interval = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await socket.SendAsync(Snapshot().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            } while (await interval.WaitForNextTickAsync(lifetime.Token));
        }
        finally
        {
            lifetime.Cancel();
            await receiver;
        }
    }

    private static async Task ReceiveAsync(WebSocket socket, CancellationTokenSource lifetime)
    {
        try
        {
            byte[] buffer = new byte[256];
            // The monitor is read-only. A data message ends the connection.
            await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
        finally { lifetime.Cancel(); }
    }

    private byte[] Snapshot()
    {
        lock (_snapshotGate)
        {
            long now = Environment.TickCount64;
            if (_snapshot.Length != 0 && now - _snapshotTime < 900) return _snapshot;

            var buffer = new ArrayBufferWriter<byte>(4096);
            using (var writer = new Utf8JsonWriter(buffer)) _dataProvider(writer);
            _snapshot = buffer.WrittenSpan.ToArray();
            _snapshotTime = now;
            return _snapshot;
        }
    }

    private static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxHeaderBytes);
        try
        {
            int read = 0, end = -1;
            while (end < 0 && read < MaxHeaderBytes)
            {
                int searchFrom = Math.Max(0, read - 3);
                int count = await stream.ReadAsync(buffer.AsMemory(read, MaxHeaderBytes - read), cancellationToken);
                if (count == 0) return null;
                read += count;
                int match = buffer.AsSpan(searchFrom, read - searchFrom).IndexOf("\r\n\r\n"u8);
                if (match >= 0) end = searchFrom + match;
            }

            if (end < 0) return null;
            string[] lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
            string[] parts = lines[0].Split(' ');
            if (parts.Length != 3 || parts[2] != "HTTP/1.1" ||
                parts[0] is not ("GET" or "POST") || !parts[1].StartsWith('/'))
                return null;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon < 1 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim()))
                    return null;
            }

            if (headers.ContainsKey("Transfer-Encoding")) return null;
            int length = 0;
            if (headers.TryGetValue("Content-Length", out var value) &&
                (!int.TryParse(value, out length) || length is < 0 or > MaxPasswordBytes))
                return null;
            if (parts[0] == "GET" && length != 0) return null;

            int start = end + 4;
            int available = read - start;
            if (available > length) return null;
            byte[] body = new byte[length];
            buffer.AsSpan(start, available).CopyTo(body);
            if (available < length)
                await stream.ReadExactlyAsync(body.AsMemory(available), cancellationToken);

            return new Request(parts[0], parts[1], headers, body);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static async Task RespondAsync(Stream stream, string status, byte[] body, CancellationToken deadline,
        string contentType = "text/plain; charset=utf-8", string headers = "")
    {
        string reply = $"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
            $"Referrer-Policy: no-referrer\r\n{headers}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), deadline);
        if (body.Length > 0) await stream.WriteAsync(body, deadline);
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body)
    {
        public string Header(string name) => Headers.GetValueOrDefault(name, "");
    }
}
