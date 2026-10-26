// A small server that misbehaves on demand, used by scripts/verify.ps1 to test Pulse for real.
//   GET  /ok                 always 200
//   GET  /flaky              answers with the status code set through POST /control/{code} (200 at start)
//   GET  /slow?ms=3000       answers 200 after the given delay
//   GET  /redirect           302 toward an internal address (Pulse must report it, never follow it)
//   POST /control/{code}     changes what /flaky answers
// It also plays the receiver of Pulse's alerts:
//   POST   /hook             a webhook endpoint that keeps what it receives
//   GET    /hook/received    what the webhook received so far (JSON)
//   DELETE /hook/received    forgets it
//   POST   /hook/fail/{n}    makes the next n webhook calls answer 500 (to test retries)
//   an SMTP server on the second argument (default 2525), with GET /smtp/received and DELETE /smtp/received
using System.Collections.Concurrent;
using System.Globalization;
using FlakyTarget;

var port = args.Length > 0 ? args[0] : "9100";
var smtpPort = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 2525;
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();

await using var smtp = new SmtpSink(smtpPort);
smtp.Start();

var flakyStatus = 200;
var hookFailures = 0;
var hooks = new ConcurrentQueue<HookCall>();

app.MapGet("/ok", () => Results.Ok("ok"));
app.MapGet("/flaky", () => Results.StatusCode(Volatile.Read(ref flakyStatus)));
app.MapGet("/slow", async (int? ms, CancellationToken ct) =>
{
    await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(ms ?? 3000, 0, 60_000)), ct);
    return Results.Ok("slow");
});
app.MapGet("/redirect", () => Results.Redirect("http://169.254.169.254/latest/meta-data/"));
app.MapPost("/control/{code:int}", (int code) =>
{
    Volatile.Write(ref flakyStatus, Math.Clamp(code, 100, 599));
    return Results.Text(flakyStatus.ToString(CultureInfo.InvariantCulture));
});

app.MapPost("/hook", async (HttpRequest request) =>
{
    if (Interlocked.Decrement(ref hookFailures) >= 0)
    {
        return Results.StatusCode(500);
    }

    Interlocked.Exchange(ref hookFailures, 0);
    using var reader = new StreamReader(request.Body);
    hooks.Enqueue(new HookCall(
        request.Headers["X-Pulse-Event"].ToString(),
        request.Headers["X-Pulse-Timestamp"].ToString(),
        request.Headers["X-Pulse-Signature"].ToString(),
        await reader.ReadToEndAsync()));
    return Results.Ok();
});
app.MapGet("/hook/received", () => Results.Ok(hooks.ToArray()));
app.MapDelete("/hook/received", () =>
{
    hooks.Clear();
    return Results.NoContent();
});
app.MapPost("/hook/fail/{count:int}", (int count) =>
{
    Volatile.Write(ref hookFailures, Math.Clamp(count, 0, 100));
    return Results.NoContent();
});

app.MapGet("/smtp/received", () => Results.Ok(smtp.Mails.ToArray()));
app.MapDelete("/smtp/received", () =>
{
    smtp.Clear();
    return Results.NoContent();
});

await app.RunAsync();

namespace FlakyTarget
{
    internal sealed record HookCall(string Event, string Timestamp, string Signature, string Body);
}
