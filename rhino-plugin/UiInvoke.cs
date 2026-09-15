using Eto.Forms;

namespace UrbanBridge.Rhino;

/// <summary>
/// Coalesces rapid UI updates so TextArea/Label churn does not flood the Eto message pump.
/// </summary>
internal static class UiInvoke
{
    private const int DefaultDelayMs = 80;

    /// <summary>
    /// Schedule <paramref name="action"/> on the UI thread; if called again before the delay,
    /// only the latest action runs (previous pending is dropped).
    /// </summary>
    public static void Coalesce(ref System.Threading.Timer? timer, object gate, Action action, int delayMs = DefaultDelayMs)
    {
        lock (gate)
        {
            timer?.Dispose();
            timer = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (Application.Instance != null)
                        Application.Instance.AsyncInvoke(action);
                    else
                        action();
                }
                catch
                {
                    try { action(); } catch { /* ignore UI teardown */ }
                }
            }, null, delayMs, System.Threading.Timeout.Infinite);
        }
    }

    public static void DisposeTimer(ref System.Threading.Timer? timer, object gate)
    {
        lock (gate)
        {
            timer?.Dispose();
            timer = null;
        }
    }

    /// <summary>Format issue lines with a hard cap to keep TextArea cheap.</summary>
    public static string FormatCappedLines(IEnumerable<string> lines, int maxLines = 40)
    {
        var list = lines as IList<string> ?? lines.ToList();
        if (list.Count == 0) return string.Empty;
        if (list.Count <= maxLines)
            return string.Join("\n", list);
        return string.Join("\n", list.Take(maxLines)) + $"\n… +{list.Count - maxLines} more";
    }
}
