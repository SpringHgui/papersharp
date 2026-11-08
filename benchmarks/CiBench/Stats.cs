namespace CiBench;

public static class Stats
{
    public static double Mean(IReadOnlyCollection<double> xs) =>
        xs.Count == 0 ? double.NaN : xs.Average();

    public static double Std(IReadOnlyCollection<double> xs, double mean)
    {
        if (xs.Count < 2) return 0;
        double sum = xs.Sum(x => (x - mean) * (x - mean));
        return Math.Sqrt(sum / (xs.Count - 1));
    }

    /// <summary>最近秩法；输入必须已升序。</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        int idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    public static List<double> Sorted(IEnumerable<double> xs) => xs.OrderBy(x => x).ToList();
}
