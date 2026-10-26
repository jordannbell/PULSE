namespace Pulse.Web;

/// <summary>
/// The short message a page shows after an action ("Service ajouté."). It fades by itself after a few
/// seconds; the page passes the way to redraw itself.
/// </summary>
public sealed class ToastState(Func<Task> redraw) : IDisposable
{
    private static readonly TimeSpan Lasts = TimeSpan.FromSeconds(6);

    private Timer? _timer;

    public string? Text { get; private set; }

    public bool IsError { get; private set; }

    public void Show(string text, bool isError = false)
    {
        Text = text;
        IsError = isError;
        _timer?.Dispose();
        _timer = new Timer(_ => Dismiss(), null, Lasts, Timeout.InfiniteTimeSpan);
        _ = redraw();
    }

    public void Dismiss()
    {
        Text = null;
        _ = redraw();
    }

    public void Dispose() => _timer?.Dispose();
}
