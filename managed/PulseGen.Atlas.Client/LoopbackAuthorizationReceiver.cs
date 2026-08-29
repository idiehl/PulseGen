using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PulseGen.Atlas.Client;

public sealed class SystemOidcBrowser : IOidcBrowser
{
    public Task OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

public sealed class LoopbackAuthorizationReceiver : IAuthorizationCallbackReceiver
{
    private const int MaximumHeaderLines = 100;
    private const int MaximumRequestLineCharacters = 16 * 1024;

    public async Task<AuthorizationCallback> ReceiveAsync(Uri redirectUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        if (redirectUri.Scheme != Uri.UriSchemeHttp || !redirectUri.IsLoopback || redirectUri.Host != "127.0.0.1")
        {
            throw new ArgumentException("The PulseGen callback receiver only accepts an IPv4 loopback HTTP URI.", nameof(redirectUri));
        }

        var listener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
        listener.Start(1);
        using var cancellationRegistration = cancellationToken.Register(listener.Stop);
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
            {
                throw new InvalidDataException("The authorization callback did not originate from the loopback interface.");
            }

            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(requestLine) || requestLine.Length > MaximumRequestLineCharacters)
            {
                await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "Invalid callback", cancellationToken);
                throw new InvalidDataException("The authorization callback request line was invalid.");
            }
            for (var lineIndex = 0; lineIndex < MaximumHeaderLines; lineIndex++)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null || line.Length == 0)
                {
                    break;
                }
                if (line.Length > MaximumRequestLineCharacters)
                {
                    await WriteResponseAsync(stream, HttpStatusCode.RequestHeaderFieldsTooLarge, "Callback headers too large", cancellationToken);
                    throw new InvalidDataException("The authorization callback contained an oversized header.");
                }
                if (lineIndex == MaximumHeaderLines - 1)
                {
                    await WriteResponseAsync(stream, HttpStatusCode.RequestHeaderFieldsTooLarge, "Callback headers too large", cancellationToken);
                    throw new InvalidDataException("The authorization callback contained too many headers.");
                }
            }

            var fields = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3 || fields[0] != "GET" || !fields[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, HttpStatusCode.MethodNotAllowed, "Invalid callback method", cancellationToken);
                throw new InvalidDataException("The authorization callback must be an HTTP GET request.");
            }
            if (!fields[1].StartsWith("/", StringComparison.Ordinal) || fields[1].StartsWith("//", StringComparison.Ordinal) ||
                !Uri.TryCreate(redirectUri, fields[1], out var callbackUri) || callbackUri.Scheme != redirectUri.Scheme ||
                callbackUri.Host != redirectUri.Host || callbackUri.Port != redirectUri.Port || callbackUri.AbsolutePath != redirectUri.AbsolutePath)
            {
                await WriteResponseAsync(stream, HttpStatusCode.NotFound, "Unknown callback path", cancellationToken);
                throw new InvalidDataException("The authorization callback path did not match PulseGen's registered redirect.");
            }

            var query = ParseQuery(callbackUri.Query);
            var callback = new AuthorizationCallback(
                query.GetValueOrDefault("code"),
                query.GetValueOrDefault("state"),
                query.GetValueOrDefault("error"),
                query.GetValueOrDefault("error_description"));
            var succeeded = string.IsNullOrWhiteSpace(callback.Error) && !string.IsNullOrWhiteSpace(callback.Code);
            await WriteResponseAsync(
                stream,
                succeeded ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
                succeeded ? "PulseGen sign-in complete. You may close this tab." : "PulseGen sign-in was not completed. Return to the workstation for details.",
                cancellationToken);
            return callback;
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var delimiter = pair.IndexOf('=');
            var key = Decode(delimiter < 0 ? pair : pair[..delimiter]);
            var value = Decode(delimiter < 0 ? string.Empty : pair[(delimiter + 1)..]);
            values.TryAdd(key, value);
        }
        return values;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static async Task WriteResponseAsync(Stream stream, HttpStatusCode status, string message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>PulseGen · Atlas</title>
            <style>:root{color-scheme:dark}body{margin:0;background:#0b0f14;color:#edf7f5;font:16px system-ui;display:grid;place-items:center;min-height:100vh}main{max-width:32rem;padding:2rem;border:1px solid #2b3c4f;border-radius:16px;background:#111721}p{color:#9fb0c2}</style></head>
            <body><main><h1>Atlas Universalis</h1><p>{{message}}</p></main></body></html>
            """);
        var reason = status == HttpStatusCode.OK ? "OK" : "Bad Request";
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
