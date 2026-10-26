using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FlakyTarget;

internal sealed record ReceivedMail(string From, IReadOnlyList<string> To, string Data);

/// <summary>
/// A minimal SMTP server that accepts every message and keeps it in memory, so e-mail alerts can be
/// tested for real without a mail provider. It speaks only what a client needs to deliver one message.
/// </summary>
internal sealed class SmtpSink : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ConcurrentQueue<ReceivedMail> _mails = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _acceptLoop = Task.CompletedTask;

    public SmtpSink(int port) => _listener = new TcpListener(IPAddress.Loopback, port);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IReadOnlyCollection<ReceivedMail> Mails => _mails;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public void Clear() => _mails.Clear();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The listener was closed on purpose.
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 sink ready");

                var from = string.Empty;
                var to = new List<string>();
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var command = line.Split(' ', 2)[0].ToUpperInvariant();
                    switch (command)
                    {
                        case "EHLO":
                        case "HELO":
                            await writer.WriteLineAsync("250 sink");
                            break;
                        case "MAIL":
                            from = Address(line);
                            to.Clear();
                            await writer.WriteLineAsync("250 OK");
                            break;
                        case "RCPT":
                            to.Add(Address(line));
                            await writer.WriteLineAsync("250 OK");
                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 end with a single dot");
                            _mails.Enqueue(new ReceivedMail(from, [.. to], await ReadDataAsync(reader)));
                            await writer.WriteLineAsync("250 queued");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 bye");
                            return;
                        case "RSET":
                        case "NOOP":
                            await writer.WriteLineAsync("250 OK");
                            break;
                        default:
                            await writer.WriteLineAsync("502 not supported");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away; nothing to keep.
            }
        }
    }

    private async Task<string> ReadDataAsync(StreamReader reader)
    {
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(_stop.Token) is { } line && line != ".")
        {
            // A line that starts with a dot is sent with one more dot in front (RFC 5321, 4.5.2).
            data.AppendLine(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line);
        }

        return data.ToString();
    }

    private static string Address(string line)
    {
        var start = line.IndexOf('<', StringComparison.Ordinal);
        var end = line.IndexOf('>', StringComparison.Ordinal);
        return start >= 0 && end > start ? line[(start + 1)..end] : line;
    }
}
