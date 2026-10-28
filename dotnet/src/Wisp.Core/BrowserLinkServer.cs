using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wisp.Core;

/// <summary>
/// A tiny HTTP/1.1 server for the zero-install browser client. It binds to the
/// LAN address, requires the one-time token from the QR URL, and exposes only
/// three things: the offline page, the received-file list/download endpoint, and
/// uploads into the same received-files folder the native engine uses.
/// </summary>
public sealed class BrowserLinkServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const long MaxUploadBytes = 8L * 1024 * 1024 * 1024;
    private const int MaxTextBytes = 64 * 1024;
    private const string PageResourceName = "Wisp.Core.BrowserPage.html";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<byte[]> PageBytes = new(LoadPage, isThreadSafe: true);

    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly object _fileGate = new();
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _acceptTask;
    private int _connectedRaised;

    public BrowserLinkServer(string rootDirectory, string deviceName, string host, int port = 0)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(RootDirectory);
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Wisp device" : deviceName.Trim();
        Host = host;
        Port = port;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Url = "";
    }

    public string RootDirectory { get; }
    public string DeviceName { get; }
    public string Host { get; }
    public int Port { get; private set; }
    public string Token { get; }
    public string Url { get; private set; }
    public bool IsRunning { get; private set; }

    public event EventHandler<string>? FileReceived;
    public event EventHandler<string>? TextReceived;
    public event EventHandler? BrowserConnected;
    public event EventHandler? Activity;

    public void Start()
    {
        if (IsRunning) return;
        if (!IPAddress.TryParse(Host, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException("Invalid local IPv4 address.");
        }

        var listener = new TcpListener(address, Port);
        listener.Start(64);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Url = $"http://{Host}:{Port}/?t={Token}";
        _cts = new CancellationTokenSource();
        IsRunning = true;
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        if (!IsRunning && _listener is null) return;
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        foreach (var client in _clients.Keys)
        {
            try { client.Close(); } catch { }
        }
        _clients.Clear();
        try { _acceptTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _acceptTask = null;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;
        if (listener is null) return;
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            _clients[client] = 0;
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            try
            {
                await using var stream = client.GetStream();
                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestCts.CancelAfter(TimeSpan.FromMinutes(30));
                await HandleRequestAsync(stream, requestCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Browser link request failed: {exception}");
            }
            finally
            {
                _clients.TryRemove(client, out _);
                try { client.Close(); } catch { }
            }
        }
    }

    private async Task HandleRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var headerBuffer = new byte[MaxHeaderBytes];
        var total = 0;
        var headerEnd = -1;
        while (total < headerBuffer.Length)
        {
            var read = await stream.ReadAsync(headerBuffer.AsMemory(total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) return;
            total += read;
            headerEnd = FindHeaderEnd(headerBuffer, total);
            if (headerEnd >= 0) break;
        }
        if (headerEnd < 0)
        {
            await WriteStatusAsync(stream, 431, "Request Header Fields Too Large", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var headerText = Encoding.ASCII.GetString(headerBuffer, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return;
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 3)
        {
            await WriteStatusAsync(stream, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
            return;
        }

        var method = requestLine[0].ToUpperInvariant();
        var target = requestLine[1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = value;
        }

        SplitTarget(target, out var path, out var query);
        var token = query.TryGetValue("t", out var queryToken) ? queryToken : null;
        headers.TryGetValue("X-Wisp-Token", out var headerToken);
        var authorized = IsAuthorized(token, headerToken);

        if (path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
        {
            await WriteStatusAsync(stream, 204, "No Content", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!authorized)
        {
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(stream, 403, new { error = "This browser link expired." },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteHtmlAsync(stream, 403, Encoding.UTF8.GetBytes(
                    "<!doctype html><meta charset=utf-8><title>Wisp link expired</title>" +
                    "<body style='font-family:system-ui;background:#0b0e12;color:#f5f8f7;" +
                    "padding:48px'><h1>Link expired</h1><p>Create a new browser link on the phone.</p>"),
                    cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (Interlocked.Exchange(ref _connectedRaised, 1) == 0)
        {
            try { BrowserConnected?.Invoke(this, EventArgs.Empty); } catch { }
        }
        try { Activity?.Invoke(this, EventArgs.Empty); } catch { }

        headers.TryGetValue("Content-Length", out var lengthText);
        long.TryParse(lengthText, out var contentLength);
        var bodyStart = headerEnd + 4;
        var leftover = Math.Max(0, total - bodyStart);

        if (method == "GET" && path is "/" or "")
        {
            await WriteHtmlAsync(stream, 200, PageBytes.Value, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path.Equals("/api/files", StringComparison.OrdinalIgnoreCase))
        {
            var files = ListFiles();
            await WriteJsonAsync(stream, 200, new
            {
                device = DeviceName,
                files,
                count = files.Count
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path.Equals("/api/download", StringComparison.OrdinalIgnoreCase))
        {
            var requested = query.TryGetValue("path", out var encodedPath) ? encodedPath : "";
            var fullPath = ResolveWithinRoot(requested);
            if (fullPath is null || !File.Exists(fullPath))
            {
                await WriteStatusAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
                return;
            }
            await WriteFileAsync(stream, fullPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path.Equals("/api/upload", StringComparison.OrdinalIgnoreCase))
        {
            var name = query.TryGetValue("name", out var uploadedName) ? uploadedName : "";
            if (contentLength < 0 || contentLength > MaxUploadBytes)
            {
                await WriteJsonAsync(stream, 413, new { error = "That file is too large." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            var saved = await SaveUploadAsync(stream, headerBuffer, bodyStart, leftover,
                contentLength, name, cancellationToken).ConfigureAwait(false);
            if (saved is null)
            {
                await WriteJsonAsync(stream, 400, new { error = "Upload failed." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            try { FileReceived?.Invoke(this, saved); } catch { }
            await WriteJsonAsync(stream, 200, new
            {
                ok = true,
                name = Path.GetFileName(saved),
                size = new FileInfo(saved).Length
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path.Equals("/api/text", StringComparison.OrdinalIgnoreCase))
        {
            if (contentLength < 0 || contentLength > MaxTextBytes)
            {
                await WriteJsonAsync(stream, 413, new { error = "That text is too long." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            var body = await ReadBodyAsync(stream, headerBuffer, bodyStart, leftover,
                contentLength, cancellationToken).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(body).Trim();
            if (text.Length == 0)
            {
                await WriteJsonAsync(stream, 400, new { error = "Nothing to send." },
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            try { TextReceived?.Invoke(this, text); } catch { }
            await WriteJsonAsync(stream, 200, new { ok = true }, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteStatusAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' &&
                buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static void SplitTarget(string target, out string path, out Dictionary<string, string> query)
    {
        var question = target.IndexOf('?');
        path = question < 0 ? target : target[..question];
        var queryText = question < 0 ? "" : target[(question + 1)..];
        query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in queryText.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            var key = equals < 0 ? part : part[..equals];
            var value = equals < 0 ? "" : part[(equals + 1)..];
            try
            {
                query[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
            }
        }
    }

    private bool IsAuthorized(string? queryToken, string? headerToken)
    {
        var presented = headerToken ?? queryToken ?? "";
        var expected = Encoding.UTF8.GetBytes(Token);
        var actual = Encoding.UTF8.GetBytes(presented);
        return expected.Length == actual.Length &&
               CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private async Task<string?> SaveUploadAsync(NetworkStream stream, byte[] headerBuffer,
        int bodyStart, int leftover, long length, string requestedName,
        CancellationToken cancellationToken)
    {
        var name = SafeFileName(requestedName);
        var partPath = Path.Combine(RootDirectory, $".wisp-upload-{Guid.NewGuid():N}.part");
        try
        {
            await using (var file = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                long written = 0;
                if (leftover > 0)
                {
                    var take = (int)Math.Min(leftover, length);
                    if (take > 0)
                    {
                        await file.WriteAsync(headerBuffer.AsMemory(bodyStart, take),
                            cancellationToken).ConfigureAwait(false);
                        written = take;
                    }
                }
                var buffer = new byte[64 * 1024];
                while (written < length)
                {
                    var want = (int)Math.Min(buffer.Length, length - written);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, want), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0) throw new IOException("Upload ended early.");
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    written += read;
                }
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            string target;
            lock (_fileGate)
            {
                target = UniquePath(name);
                File.Move(partPath, target);
            }
            return target;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Browser upload failed: {exception}");
            try { File.Delete(partPath); } catch { }
            return null;
        }
    }

    private static async Task<byte[]> ReadBodyAsync(NetworkStream stream, byte[] headerBuffer,
        int bodyStart, int leftover, long length, CancellationToken cancellationToken)
    {
        var capacity = (int)Math.Max(0, Math.Min(length, MaxTextBytes));
        using var memory = new MemoryStream(capacity);
        if (leftover > 0)
        {
            var take = (int)Math.Min(leftover, length);
            if (take > 0) memory.Write(headerBuffer, bodyStart, take);
        }
        var remaining = length - memory.Length;
        var buffer = new byte[8192];
        while (remaining > 0)
        {
            var want = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, want), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            memory.Write(buffer, 0, read);
            remaining -= read;
        }
        return memory.ToArray();
    }

    private List<BrowserFile> ListFiles()
    {
        var files = new List<BrowserFile>();
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System,
                MaxRecursionDepth = 6
            };
            foreach (var path in Directory.EnumerateFiles(RootDirectory, "*", options))
            {
                var name = Path.GetFileName(path);
                if (name.Contains(".wisp-", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    var info = new FileInfo(path);
                    var relative = Path.GetRelativePath(RootDirectory, path).Replace('\\', '/');
                    files.Add(new BrowserFile(relative, name, info.Length,
                        info.LastWriteTimeUtc, KindOf(name)));
                }
                catch
                {
                }
                if (files.Count >= 1000) break;
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Browser file list failed: {exception}");
        }
        return files.OrderByDescending(file => file.Modified).ToList();
    }

    private string UniquePath(string name)
    {
        var candidate = Path.Combine(RootDirectory, name);
        if (!File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 2; i < 10_000; i++)
        {
            candidate = Path.Combine(RootDirectory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(RootDirectory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    private string? ResolveWithinRoot(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            var combined = Path.GetFullPath(Path.Combine(RootDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var root = RootDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? RootDirectory
                : RootDirectory + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return combined.StartsWith(root, comparison) && File.Exists(combined)
                ? combined
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeFileName(string name)
    {
        name = Path.GetFileName(name ?? "");
        if (string.IsNullOrWhiteSpace(name)) name = "file";
        var invalid = Path.GetInvalidFileNameChars();
        var filtered = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            filtered.Append(invalid.Contains(character) || character is '\r' or '\n' or '\0'
                ? '_'
                : character);
        }
        var value = filtered.ToString().Trim().Trim('.');
        if (value.Length == 0) value = "file";
        if (value.Length > 180)
        {
            var extension = Path.GetExtension(value);
            value = value[..Math.Min(160, value.Length)] + extension;
        }
        return value;
    }

    private static string KindOf(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".heic" or ".heif" => "image",
            ".mp4" or ".m4v" or ".mkv" or ".webm" or ".3gp" or ".avi" or ".mov" or ".ts" => "video",
            ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" or ".wav" or ".flac" or ".amr" => "audio",
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or
                ".txt" or ".md" or ".csv" or ".rtf" or ".epub" or ".json" or ".xml" => "document",
            _ => "file"
        };
    }

    private static byte[] LoadPage()
    {
        var assembly = typeof(BrowserLinkServer).Assembly;
        using var stream = assembly.GetManifestResourceStream(PageResourceName)
            ?? throw new InvalidOperationException("The browser page resource is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static Task WriteStatusAsync(NetworkStream stream, int status, string reason,
        CancellationToken cancellationToken) =>
        WriteBodyAsync(stream, status, reason, "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes(reason + "\n"), cancellationToken);

    private static Task WriteHtmlAsync(NetworkStream stream, int status, byte[] body,
        CancellationToken cancellationToken) =>
        WriteBodyAsync(stream, status, StatusText(status), "text/html; charset=utf-8", body,
            cancellationToken,
            "Content-Security-Policy: default-src 'self'; img-src 'self' data:; " +
            "style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; " +
            "base-uri 'none'; form-action 'self'\r\n" +
            "X-Frame-Options: DENY\r\n");

    private static Task WriteJsonAsync(NetworkStream stream, int status, object body,
        CancellationToken cancellationToken) =>
        WriteBodyAsync(stream, status, StatusText(status), "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions), cancellationToken);

    private static async Task WriteBodyAsync(NetworkStream stream, int status, string reason,
        string contentType, byte[] body, CancellationToken cancellationToken,
        string? extraHeaders = null)
    {
        var headers = new StringBuilder();
        headers.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        headers.Append("Content-Type: ").Append(contentType).Append("\r\n");
        headers.Append("Content-Length: ").Append(status == 204 ? 0 : body.Length).Append("\r\n");
        headers.Append("Cache-Control: no-store\r\n");
        headers.Append("X-Content-Type-Options: nosniff\r\n");
        headers.Append("Referrer-Policy: no-referrer\r\n");
        headers.Append("Connection: close\r\n");
        if (extraHeaders is not null) headers.Append(extraHeaders);
        headers.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken)
            .ConfigureAwait(false);
        if (status != 204 && body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteFileAsync(NetworkStream stream, string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var safeName = info.Name.Replace("\"", "");
        var encodedName = Uri.EscapeDataString(info.Name);
        var headers = new StringBuilder();
        headers.Append("HTTP/1.1 200 OK\r\n");
        headers.Append("Content-Type: ").Append(ContentTypeOf(info.Name)).Append("\r\n");
        headers.Append("Content-Length: ").Append(info.Length).Append("\r\n");
        headers.Append("Content-Disposition: attachment; filename=\"").Append(safeName)
            .Append("\"; filename*=UTF-8''").Append(encodedName).Append("\r\n");
        headers.Append("Cache-Control: no-store\r\n");
        headers.Append("X-Content-Type-Options: nosniff\r\n");
        headers.Append("X-Frame-Options: DENY\r\n");
        headers.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken)
            .ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await file.CopyToAsync(stream, 64 * 1024, cancellationToken).ConfigureAwait(false);
    }

    private static string ContentTypeOf(string name) =>
        Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".pdf" => "application/pdf",
            ".txt" or ".md" => "text/plain; charset=utf-8",
            ".json" => "application/json",
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".csv" => "text/csv",
            _ => "application/octet-stream"
        };

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        413 => "Payload Too Large",
        431 => "Request Header Fields Too Large",
        _ => "Error"
    };
}

public sealed record BrowserFile(
    string Path,
    string Name,
    long Size,
    DateTimeOffset Modified,
    string Kind);
