using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MMServerAgent;

/// <summary>A request as the agent's handlers see it.</summary>
public sealed record HttpRequestInfo(string Method, string Path, IPAddress RemoteAddress, byte[] Body, string ContentType, bool Encrypted = false);

/// <summary>A response from a handler.</summary>
public sealed record HttpReply(int Status, string ContentType, string Body, byte[]? Bytes = null, string? ContentEncoding = null);

/// <summary>
/// A deliberately tiny web server: GET for the server list, plus POST bodies only up to a
/// fixed size (problem reports). Small header limits and short timeouts so a slow or hostile
/// client can't tie it up. No admin URL reservation needed on Windows.
///
/// HTTPS and plain HTTP share the one port: a TLS handshake always starts with byte 0x16,
/// which no HTTP request does, so the first byte says which one the client speaks.
/// </summary>
public sealed class MiniHttpServer(
    int port,
    long maxBodyBytes,
    Func<HttpRequestInfo, Task<HttpReply>> handler,
    Func<string, string, IPAddress, long, HttpReply?>? beforeBody = null,
    X509Certificate2? certificate = null,
    bool allowPlain = true)
{
    private const int MaxHeaderBytes = 8 * 1024;
    private const int MaxConnectionsPerAddress = 4;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IPAddress, int> _perAddress = new();
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(120);
    private readonly SemaphoreSlim _slots = new(32);

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { break; }

                if (!_slots.Wait(0)) { client.Dispose(); continue; } // too busy; drop

                // One address can't hold every slot (slow-upload / slowloris style tie-up).
                var address = Normalize((client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None);
                if (_perAddress.AddOrUpdate(address, 1, (_, n) => n + 1) > MaxConnectionsPerAddress)
                {
                    Release(address);
                    client.Dispose();
                    _slots.Release();
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try { await HandleAsync(client, ct); }
                    catch { /* bad client; ignore */ }
                    finally
                    {
                        await LingerCloseAsync(client);
                        client.Dispose();
                        Release(address);
                        _slots.Release();
                    }
                }, CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// "Lingering close": after replying, stop sending and quietly read whatever the client
    /// is still sending (briefly, and bounded). Closing a socket with unread data makes Windows
    /// reset the connection, which throws away the reply, so a client refused early (rate
    /// limit, too large, wrong path) would see "connection reset" instead of the reason.
    /// </summary>
    private static async Task LingerCloseAsync(TcpClient client)
    {
        try
        {
            var socket = client.Client;
            if (!socket.Connected) return;
            socket.Shutdown(SocketShutdown.Send);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var sink = new byte[16 * 1024];
            long drained = 0;
            while (drained < 16L * 1024 * 1024)
            {
                int n = await socket.ReceiveAsync(sink, SocketFlags.None, cts.Token);
                if (n == 0) break;
                drained += n;
            }
        }
        catch { /* timed out or already closed; fine */ }
    }

    private void Release(IPAddress address)
    {
        if (_perAddress.AddOrUpdate(address, 0, (_, n) => n - 1) <= 0)
            _perAddress.TryRemove(new KeyValuePair<IPAddress, int>(address, 0));
    }

    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        Stream stream = client.GetStream();
        var remote = Normalize((client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None);
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(HeaderTimeout);

        // ---- HTTPS or plain?
        var first = new byte[1];
        if (await client.Client.ReceiveAsync(first, SocketFlags.Peek, headerCts.Token) == 0) return;
        bool encrypted = false;
        if (first[0] == 0x16 && certificate != null)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, headerCts.Token);
            stream = ssl;
            encrypted = true;
        }
        else if (!allowPlain)
        {
            await WriteAsync(stream, new HttpReply(400, "application/json",
                "{\"ok\":false,\"message\":\"This MM server agent only accepts encrypted connections. Update the MM Launcher.\"}"), ct);
            return;
        }

        // ---- headers
        var buffer = new byte[MaxHeaderBytes];
        int read = 0, headerEnd = -1;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), headerCts.Token);
            if (n == 0) break;
            read += n;
            headerEnd = IndexOf(buffer, read, "\r\n\r\n"u8);
            if (headerEnd >= 0) break;
        }
        if (headerEnd < 0) { await WriteAsync(stream, new HttpReply(400, "text/plain", "Bad request."), ct); return; }

        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) { await WriteAsync(stream, new HttpReply(400, "text/plain", "Bad request."), ct); return; }
        var method = requestLine[0].ToUpperInvariant();
        var path = requestLine[1].Split('?')[0];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        // ---- body (POST only, bounded)
        byte[] body = [];
        if (method == "POST")
        {
            if (!headers.TryGetValue("Content-Length", out var lenText) || !long.TryParse(lenText, out var length) || length < 0)
            {
                await WriteAsync(stream, new HttpReply(411, "text/plain", "Content-Length required."), ct);
                return;
            }
            if (length > maxBodyBytes)
            {
                await WriteAsync(stream, new HttpReply(413, "application/json",
                    $"{{\"ok\":false,\"message\":\"Report too large ({length / 1024 / 1024} MB). The limit is {maxBodyBytes / 1024 / 1024} MB.\"}}"), ct);
                return;
            }
            // Let the handler refuse (wrong path, rate limit) before receiving the body.
            if (beforeBody?.Invoke(method, path, remote, length) is { } early)
            {
                await WriteAsync(stream, early, ct);
                return;
            }
            body = new byte[length];
            int already = Math.Min(read - (headerEnd + 4), (int)length);
            Buffer.BlockCopy(buffer, headerEnd + 4, body, 0, Math.Max(0, already));
            using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bodyCts.CancelAfter(BodyTimeout);
            int got = Math.Max(0, already);
            while (got < length)
            {
                int n = await stream.ReadAsync(body.AsMemory(got), bodyCts.Token);
                if (n == 0) return; // client hung up
                got += n;
            }
        }
        else if (method != "GET")
        {
            await WriteAsync(stream, new HttpReply(405, "text/plain", "Method not allowed."), ct);
            return;
        }

        headers.TryGetValue("Content-Type", out var contentType);
        var reply = await handler(new HttpRequestInfo(method, path, remote, body, contentType ?? "", encrypted));
        await WriteAsync(stream, reply, ct);
    }

    private static async Task WriteAsync(Stream stream, HttpReply reply, CancellationToken ct)
    {
        var bodyBytes = reply.Bytes ?? Encoding.UTF8.GetBytes(reply.Body);
        var reason = reply.Status switch
        {
            200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
            411 => "Length Required", 413 => "Payload Too Large", 415 => "Unsupported Media Type", 429 => "Too Many Requests",
            503 => "Service Unavailable", _ => "Error",
        };
        var header = $"HTTP/1.1 {reply.Status} {reason}\r\nContent-Type: {reply.ContentType}; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\n" +
                     (reply.ContentEncoding != null ? $"Content-Encoding: {reply.ContentEncoding}\r\n" : "") +
                     "Cache-Control: no-store\r\nConnection: close\r\nServer: MMServerAgent\r\n\r\n";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(bodyBytes.Length > 1024 * 1024 ? 90 : 15));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cts.Token);
        await stream.WriteAsync(bodyBytes, cts.Token);
    }

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern) =>
        data.AsSpan(0, length).IndexOf(pattern);
}
