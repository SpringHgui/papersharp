using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CiBench;

/// <summary>
/// 后台线程按固定间隔采样「本进程 + 全部后代进程」的常驻内存总和。
/// 这是横向比较能否成立的关键：PuppeteerSharp / IronPdf 的 Chrome 是多进程树，
/// 只量 .NET 宿主进程会把浏览器的几百 MB 漏掉。
/// Windows 走 Toolhelp32 建立父子关系后只对树内 pid 取工作集（绝不遍历系统全部进程），
/// Linux 走 /proc/*/stat；两者都有「至少取到自身 RSS」的保底，避免出现假的 0MB。
/// </summary>
public sealed class RssSampler
{
    readonly int _intervalMs;
    readonly int _selfPid = Environment.ProcessId;
    Thread? _thread;
    volatile bool _stop;
    double _peakMb;
    double _lastMb;
    readonly Queue<double> _window = new();
    int _suspectedSpikes;

    public RssSampler(int intervalMs = 25) => _intervalMs = Math.Max(5, intervalMs);

    public void Start()
    {
        _peakMb = 0;
        _lastMb = 0;
        _stop = false;
        lock (_window) _window.Clear();
        Interlocked.Exchange(ref _suspectedSpikes, 0);
        _thread = new Thread(Loop) { IsBackground = true, Name = "cibench-rss" };
        _thread.Start();
    }

    /// <summary>停止采样并返回（峰值 MB, 最后一次采样 MB）。</summary>
    public (double PeakMb, double LastMb) Stop()
    {
        _stop = true;
        _thread?.Join(2000);
        _thread = null;
        int spikes = Interlocked.Exchange(ref _suspectedSpikes, 0);
        if (spikes > 0)
            Console.Error.WriteLine($"[rss] 抑制 {spikes} 个疑似单点尖峰（进程树快照抖动，非持续内存）");
        return (_peakMb, _lastMb);
    }

    void Loop()
    {
        while (!_stop)
        {
            try { Observe(SampleProcessTreeMb(_selfPid)); }
            catch { /* 快照瞬间进程退出，忽略本轮 */ }
            Thread.Sleep(_intervalMs);
        }
        // 收尾再采一次，避免短任务（< 一个采样间隔）拿到 0
        try { Observe(SampleProcessTreeMb(_selfPid)); }
        catch { }
    }

    /// <summary>
    /// 用最近 3 次采样的中位数定峰值。Windows Toolhelp32 的 th32ParentProcessID 是快照值，
    /// Chrome 子进程频繁生灭时 PID 复用可能让某一轮把无关的大进程误挂到树上，
    /// 产生「25ms 内涨 2~3 倍又立刻回落」的假尖峰。真实内存不会这样，中位数可抑制它，
    /// 同时保留持续上升的真实峰值。
    /// </summary>
    void Observe(double mb)
    {
        _window.Enqueue(mb);
        while (_window.Count > 3) _window.Dequeue();
        double smoothed = MedianOfWindow();
        if (_window.Count == 3 && mb > smoothed * 3 && mb > _peakMb * 2)
            Interlocked.Increment(ref _suspectedSpikes);
        _lastMb = smoothed;
        if (smoothed > _peakMb) _peakMb = smoothed;
    }

    double MedianOfWindow()
    {
        var a = _window.ToArray();
        Array.Sort(a);
        return a.Length switch
        {
            1 => a[0],
            2 => (a[0] + a[1]) / 2.0,
            _ => a[1],
        };
    }

    public static double SampleProcessTreeMb(int rootPid)
    {
        Dictionary<int, int> parents;
        Dictionary<int, long> rssKb = new();

        if (OperatingSystem.IsWindows())
        {
            parents = WindowsParentMap();
        }
        else if (OperatingSystem.IsLinux())
        {
            (parents, rssKb) = LinuxProcMap();
        }
        else
        {
            return SelfWorkingSetKb() / 1024.0;
        }

        var children = new Dictionary<int, List<int>>();
        foreach (var (pid, ppid) in parents)
        {
            if (pid == ppid) continue;
            if (!children.TryGetValue(ppid, out var list)) children[ppid] = list = new List<int>();
            list.Add(pid);
        }

        var tree = new List<int>();
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(rootPid);
        while (stack.Count > 0)
        {
            int pid = stack.Pop();
            if (!seen.Add(pid)) continue;
            tree.Add(pid);
            if (children.TryGetValue(pid, out var kids))
                foreach (var k in kids) stack.Push(k);
        }

        long totalKb = 0;
        foreach (int pid in tree)
        {
            if (rssKb.TryGetValue(pid, out long kb)) { totalKb += kb; continue; }
            if (!OperatingSystem.IsWindows()) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                totalKb += p.WorkingSet64 / 1024;
            }
            catch { /* 进程刚退出 */ }
        }

        // 保底：Toolhelp 快照失败 / 父子关系里找不到树根时，至少把自身算上
        if (totalKb <= 0 && rootPid == Environment.ProcessId)
            totalKb = SelfWorkingSetKb();

        DiagnoseOnce(parents.Count, tree.Count, totalKb);
        return totalKb / 1024.0;
    }

    static long SelfWorkingSetKb()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return p.WorkingSet64 / 1024;
        }
        catch { return 0; }
    }

    static int _diagnosed;
    static void DiagnoseOnce(int parents, int tree, long totalKb)
    {
        if (Interlocked.Exchange(ref _diagnosed, 1) != 0) return;
        Console.Error.WriteLine($"[rss] platform={(OperatingSystem.IsWindows() ? "toolhelp32" : "/proc")}, " +
            $"parents={parents}, tree={tree}, total={totalKb / 1024.0:F1}MB");
    }

    static (Dictionary<int, int> Parents, Dictionary<int, long> RssKb) LinuxProcMap()
    {
        var parents = new Dictionary<int, int>();
        var rss = new Dictionary<int, long>();
        long pageSizeKb = Math.Max(1, Environment.SystemPageSize / 1024);
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                int close = stat.LastIndexOf(')');
                if (close < 0) continue;
                // ')' 之后从 field3(state) 开始：index 1 = ppid(field4)，index 21 = rss(field24)
                var f = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length <= 21) continue;
                if (!int.TryParse(f[1], out var ppid)) continue;
                if (!long.TryParse(f[21], out var pages)) continue;
                parents[pid] = ppid;
                rss[pid] = pages * pageSizeKb;
            }
            catch { /* /proc/<pid> 在读取间消失 */ }
        }
        return (parents, rss);
    }

    static Dictionary<int, int> WindowsParentMap()
    {
        var map = new Dictionary<int, int>();
        IntPtr snap;
        try { snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0); }
        catch { return map; }
        if (snap == IntPtr.Zero || snap == (IntPtr)(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref e)) return map;
            do
            {
                if (e.th32ProcessID != e.th32ParentProcessID)
                    map[e.th32ProcessID] = e.th32ParentProcessID;
            } while (Process32Next(snap, ref e));
        }
        catch { /* 快照枚举失败，交给保底 */ }
        finally { CloseHandle(snap); }
        return map;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct PROCESSENTRY32
    {
        public int dwSize;
        public int cntUsage;
        public int th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public int th32ModuleID;
        public int cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    const uint TH32CS_SNAPPROCESS = 0x2;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
