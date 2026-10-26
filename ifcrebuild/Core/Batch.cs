namespace IfcRebuild;

/// <summary>
/// Picks the part of an input list one call works on, so that a long build runs in batches:
/// "" = all | "names=a,b" | "max=n" (first n) | "range=a-b" (items a to b-1 in input order) | a builder-specific
/// "key=value" handled by <paramref name="other"/>.
/// </summary>
internal static class Batch
{
    public static List<T> Pick<T>(List<T> items, string filter, Func<T, string> name, Func<string, string, T, bool> other = null)
    {
        if (string.IsNullOrWhiteSpace(filter)) return items;
        int eq = filter.IndexOf('=');
        if (eq < 0) throw new ArgumentException($"filter '{filter}': expected key=value");
        string k = filter[..eq].Trim(), v = filter[(eq + 1)..].Trim();
        switch (k)
        {
            case "names":
                var ns = v.Split(',').Select(s => s.Trim()).ToHashSet();
                return items.Where(x => ns.Contains(name(x))).ToList();
            case "max":
                return items.Take(int.Parse(v)).ToList();
            case "range":
                var r = v.Split('-').Select(int.Parse).ToArray();
                if (r.Length != 2 || r[1] < r[0]) throw new ArgumentException($"filter '{filter}': expected range=a-b with a <= b");
                return items.Skip(r[0]).Take(r[1] - r[0]).ToList();
            default:
                if (other == null) throw new ArgumentException($"filter key '{k}' is not supported by this method");
                return items.Where(x => other(k, v, x)).ToList();
        }
    }

    public static int Int(string s, int def) => string.IsNullOrWhiteSpace(s) ? def : int.Parse(s);
}

/// <summary>Progress of the running job, shown by ifcrebuild_status while Revit is busy.</summary>
public static class Progress
{
    /// <summary>Set by the job runner (also through reflection on a separately built copy of this assembly).</summary>
    public static Action<string> Sink;

    public static void Report(string text) => Sink?.Invoke(text);
}
