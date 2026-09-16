using Eto.Forms;

namespace UrbanBridge.Plugin;

/// <summary>Coalesced UI updates on the Rhino/Eto main thread.</summary>
public static class UiInvoke
{
    public static void Coalesce(ref System.Threading.Timer? timer, object gate, Action action, int ms = 120)
    {
        lock (gate)
        {
            timer?.Dispose();
            timer = new System.Threading.Timer(_ =>
            {
                try
                {
                    Application.Instance.AsyncInvoke(action);
                }
                catch { /* UI may be tearing down */ }
            }, null, ms, System.Threading.Timeout.Infinite);
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

    public static string FormatCappedLines(IEnumerable<string> lines, int max = 80)
    {
        var list = lines.ToList();
        if (list.Count <= max) return string.Join("\n", list);
        return string.Join("\n", list.Take(max)) + $"\n… and {list.Count - max} more";
    }
}
