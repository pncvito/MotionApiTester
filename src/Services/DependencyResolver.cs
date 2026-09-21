using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MotionApiTester.Services
{
    /// <summary>
    /// 程序集依赖解析（AssemblyResolve 的实际逻辑）+ 依赖体检。
    ///
    /// 典型场景：BinocRainbow 用 Costura 嵌入了 BaseTester，当它内部类型被反射时，
    /// CLR 在 LoadFile 上下文里找不到 BaseTester 的文件路径版 → FileNotFoundException。
    /// 这里按"已登记的字节缓存 → 搜索路径"顺序返回程序集。
    ///
    /// 搜索路径全部在运行时推导，代码里不写死任何开发机绝对路径；
    /// 额外的私有依赖目录请通过 settings.json 的 ExtraDependencySearchPaths 配置。
    ///
    /// 解析失败时**必须留下痕迹**：CLR 抛出的 FileNotFoundException 只会说"找不到 X"，
    /// 不会说去哪找过 —— 这里把搜索路径一并写进实时日志，见 <see cref="BuildMissReport"/>。
    /// </summary>
    public class DependencyResolver
    {
        private readonly Dictionary<string, byte[]> _byteCache = new Dictionary<string, byte[]>();
        private readonly List<string> _searchPaths = new List<string>();

        /// <summary>
        /// 已成功激活 Costura 的宿主程序集（机型 DLL）。
        /// 它们内嵌的 <c>costura.*.dll.compressed</c> 资源可以按需供出依赖 ——
        /// 体检必须把这份清单算作"能解析"，否则会把纯嵌入的项目误报成缺失。
        /// </summary>
        private readonly List<Assembly> _embeddedProviders = new List<Assembly>();

        /// <summary>已报告过的结果 key（同一依赖不重复刷屏）；AssemblyResolve 可能来自任意线程，故加锁</summary>
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _reportGate = new object();

        /// <summary>CLR 运行时目录（mscorlib / System.* 等所在）与其 WPF 子目录</summary>
        private static readonly string RuntimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        private static readonly string WpfDir = Path.Combine(RuntimeDir, "WPF");

        public DependencyResolver()
        {
            // 本程序自身输出目录：CLR 默认探测路径里有它，体检时必须一并认账，
            // 否则会把"其实能解析到"的共享依赖误报成缺失
            AddSearchPath(AppDomain.CurrentDomain.BaseDirectory);

            // CopyToTemp 等流程落地的临时 DLL
            AddSearchPath(Path.Combine(Path.GetTempPath(), "MotionApiTester"));

            // NuGet 全局包目录：环境变量优先，否则用默认位置
            var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (string.IsNullOrWhiteSpace(nugetRoot))
            {
                nugetRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            }
            AddSearchPath(nugetRoot);
        }

        /// <summary>当前搜索路径（按优先级排列，便于排障时输出）</summary>
        public IReadOnlyList<string> SearchPaths => _searchPaths;

        /// <summary>诊断输出（接到实时日志面板；未挂载则静默）</summary>
        public Action<string> OnDiagnostic { get; set; }

        /// <summary>
        /// 登记设备目录并提到最高优先级。
        /// 加载设备后调用，让机型 DLL 的依赖能优先在同目录解析。
        /// </summary>
        public void SetDeviceDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;

            _searchPaths.Remove(directory);
            _searchPaths.Insert(0, directory);
        }

        /// <summary>追加搜索路径（末尾，优先级最低）</summary>
        public void AddSearchPath(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            if (_searchPaths.Contains(directory)) return;
            _searchPaths.Add(directory);
        }

        /// <summary>登记 DLL 字节：加载设备时先 ReadAllBytes，后续解析直接复用，避免再次读盘</summary>
        public void Register(string simpleName, byte[] bytes)
        {
            if (string.IsNullOrEmpty(simpleName) || bytes == null) return;
            _byteCache[simpleName] = bytes;
        }

        /// <summary>
        /// 登记一个「已激活成功」的 Costura 宿主（见 <see cref="CosturaActivator.TryActivate"/>）。
        /// ⚠️ 只登记激活成功的：包内确实有资源、但 Attach 失败时那些资源是死的，
        /// 这时候把依赖算作"满足"就是撒谎，应该照实报缺失、让用户去补文件。
        /// </summary>
        public void RegisterEmbeddedProvider(Assembly host)
        {
            if (host == null) return;
            if (_embeddedProviders.Contains(host)) return;
            _embeddedProviders.Add(host);
        }

        /// <summary>清空字节缓存、嵌入宿主与已报告记录（卸载 / 切换设备时调用）</summary>
        public void Clear()
        {
            _byteCache.Clear();
            _embeddedProviders.Clear();
            lock (_reportGate) _reported.Clear();
        }

        /// <summary>
        /// AssemblyResolve 入口。
        /// 解析不到返回 null，交给 CLR 继续按默认规则查找。
        /// </summary>
        public Assembly Resolve(string assemblyFullName)
        {
            // Name 可能是 "OptoFidelity.BaseTester" 或 "OptoFidelity.BaseTester, Version=..."
            var simpleName = assemblyFullName?.Split(',')[0].Trim();
            if (string.IsNullOrEmpty(simpleName)) return null;

            try
            {
                // 1. 加载时登记的字节缓存
                if (_byteCache.TryGetValue(simpleName, out var bytes))
                {
                    var fromBytes = Assembly.Load(bytes);
                    ReportOnce($"HIT:{simpleName}", $"✓ 依赖解析 {simpleName} → 已登记的字节缓存");
                    return fromBytes;
                }

                // 2. 依次尝试各搜索路径
                var path = LocateFile(simpleName);
                if (path != null)
                {
                    ReportOnce($"HIT:{simpleName}", $"✓ 依赖解析 {simpleName} → {path}");
                    return Assembly.LoadFrom(path);
                }

                // 3. 解析不到 —— 把"搜过哪些目录"打出来，否则只看到 CLR 的方言报错
                ReportOnce($"MISS:{simpleName}", BuildMissReport(simpleName));
            }
            catch (Exception ex)
            {
                ReportOnce($"ERR:{simpleName}", $"✗ 依赖解析异常 {simpleName}: {ex.GetType().Name}: {ex.Message}");
                LogResolveError(assemblyFullName, ex);
            }

            return null;
        }

        /// <summary>
        /// 依赖体检：枚举给定程序集的直接引用并分类，把「设备目录缺运行库」这件事**一次性**说清楚。
        /// 否则要靠用户点到某个成员、由 CLR 抛 FileNotFoundException 才暴露，且一次只暴露一个（栈顶那个）。
        ///
        /// <para><b>⚠️ 判断"能不能解析"有四条途径，少算任何一条都会误报。</b></para>
        /// <list type="number">
        /// <item>已登记字节缓存 / 已在本进程里（CLR 实际加载过的）</item>
        /// <item>宿主程序集内嵌的 Costura 资源 —— 见 <see cref="RegisterEmbeddedProvider"/>。</item>
        /// <item>搜索路径里能找到文件</item>
        /// <item><b>直接请 CLR 解析一次</b>（<see cref="TryLoadOnDemand"/>）—— 唯一的权威判据。</item>
        /// </list>
        /// <para>前三条都只是"估计"：早期版本只有 2+3，纯 Costura 项目（设备目录只有 4 个文件）
        /// 会被报成"缺 7 个运行库"；补了第 2 条后仍不够 —— 第 2 条依赖"登记标志"，
        /// 而 Costura 的解析器还能被宿主 <c>&lt;Module&gt;::.cctor</c> 自动挂上，
        /// 那种途径不在登记范围内，标志一失准就又误报。第 4 条把结论交给 CLR，才真正封住这类误报。</para>
        /// </summary>
        public DependencyAudit AuditDependencies(IEnumerable<Assembly> assemblies)
        {
            var resolved = new List<string>();
            var fromEmbedded = new List<string>();
            var onDemand = new List<string>();
            var missing = new List<string>();
            if (assemblies == null) return new DependencyAudit(resolved, fromEmbedded, onDemand, missing);

            var embeddedNames = CollectEmbeddedNames();

            foreach (var asm in assemblies)
            {
                if (asm == null) continue;

                AssemblyName[] references;
                try { references = asm.GetReferencedAssemblies(); }
                catch { continue; }

                foreach (var reference in references)
                {
                    var name = reference?.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (IsFrameworkAssembly(name)) continue;
                    if (resolved.Contains(name) || fromEmbedded.Contains(name)
                        || onDemand.Contains(name) || missing.Contains(name)) continue;

                    // ① 字节缓存 / 已在本进程
                    if (_byteCache.ContainsKey(name) || IsLoadedInProcess(name))
                    {
                        resolved.Add(name);
                        continue;
                    }

                    // ② 宿主内嵌的 Costura 资源（归因用；磁盘上没有文件但按需就能解出来）
                    if (embeddedNames.Contains(name))
                    {
                        fromEmbedded.Add(name);
                        continue;
                    }

                    // ③ 文件系统
                    if (CanLocate(name))
                    {
                        resolved.Add(name);
                        continue;
                    }

                    // ④ 最后一道判据 —— 直接问 CLR。
                    //    ⚠️ 这一条是**权威**判据：前面三条都是"我估计能不能解析"，
                    //    只有这里是真的请 CLR 走一遍完整解析链（默认探测 → AssemblyResolve
                    //    → 我们自己的 resolver → Costura 内嵌资源）。
                    //    加它是因为前三条例外太多：例如 Costura 的解析器除了我们主动 Attach，
                    //    还会被宿主程序集的 <Module>::.cctor 在设备代码首次执行时自动挂上 ——
                    //    那种情况下"没登记内嵌宿主"不等于"解不出来"，只按前三条判会误报缺失。
                    if (TryLoadOnDemand(name))
                    {
                        onDemand.Add(name);
                        continue;
                    }

                    missing.Add(name);
                }
            }

            var audit = new DependencyAudit(resolved, fromEmbedded, onDemand, missing);
            WriteAuditLog(audit);
            return audit;
        }

        /// <summary>
        /// 把体检结果追加到 <c>%TEMP%\MotionApiTester\audit.log</c>。
        ///
        /// <para>为什么需要它：体检报"缺"时，界面上只有一行文字，无法区分是"真缺运行库"
        /// 还是"判据漏掉了某条解析途径"。事后排查只能靠猜 —— 这次就栽在这上面。
        /// 落盘后每次加载的四个桶都留痕，一眼能看出是哪一条途径没命中。</para>
        /// </summary>
        private static void WriteAuditLog(DependencyAudit audit)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "MotionApiTester");
                Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 总 {audit.Total} | " +
                              $"{audit.Summary} | 缺失 {audit.Missing.Count}");
                foreach (var n in audit.Resolved) sb.AppendLine($"    直接可见  {n}");
                foreach (var n in audit.FromEmbedded) sb.AppendLine($"    内嵌资源  {n}");
                foreach (var n in audit.ResolvedOnDemand) sb.AppendLine($"    按需解析  {n}");
                foreach (var n in audit.Missing) sb.AppendLine($"    ★缺失     {n}");

                File.AppendAllText(Path.Combine(dir, "audit.log"), sb.ToString());
            }
            catch { /* 诊断落盘失败不应影响体检结果 */ }
        }

        /// <summary>
        /// 请 CLR 真的解析一次（部分名称加载会走完整的解析链，含 AssemblyResolve 事件）。
        /// 成功即说明"这个依赖实际可用"，失败才是真缺。副作用是会把该程序集载入进程 ——
        /// 但那正是调用设备 API 时必然会发生的事，且体检每次加载设备只跑一遍。
        /// </summary>
        private bool TryLoadOnDemand(string simpleName)
        {
            try
            {
                var asm = Assembly.Load(simpleName);
                if (asm == null) return false;

                ReportOnce($"PROBE:{simpleName}",
                    $"✓ 体检按需解析 {simpleName} → {(asm.Location.Length == 0 ? "嵌入资源(内存)" : asm.Location)}");
                return true;
            }
            catch
            {
                // 真解不出来 —— 保持安静，由调用方汇总成一行"缺失"报告
                return false;
            }
        }

        /// <summary>汇总所有已激活宿主内嵌的依赖名（小写去重，与程序集简名不区分大小写比较）</summary>
        private HashSet<string> CollectEmbeddedNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var host in _embeddedProviders.ToArray())
            {
                foreach (var n in CosturaActivator.GetEmbeddedAssemblyNames(host))
                    names.Add(n);
            }
            return names;
        }

        /// <summary>在各搜索路径里找 simpleName.dll；找不到返回 null</summary>
        private string LocateFile(string simpleName)
        {
            foreach (var dir in _searchPaths.ToArray())
            {
                if (!Directory.Exists(dir)) continue;

                var direct = Path.Combine(dir, simpleName + ".dll");
                if (File.Exists(direct)) return direct;

                // NuGet 布局：包名/版本/lib/netXXX/xxx.dll
                foreach (var packageDir in Directory.GetDirectories(dir, simpleName + "*", SearchOption.TopDirectoryOnly))
                {
                    var dll = Directory.GetFiles(packageDir, simpleName + ".dll", SearchOption.AllDirectories).FirstOrDefault();
                    if (dll != null) return dll;
                }
            }
            return null;
        }

        /// <summary>LocateFile 的安全版：路径异常一律当作"找不到"（体检不该因为某个目录不可读而中断）</summary>
        private bool CanLocate(string simpleName)
        {
            try { return LocateFile(simpleName) != null; }
            catch { return false; }
        }

        /// <summary>该程序集是否已在当前进程里（框架程序集通常是按需加载的，不能只看磁盘）</summary>
        private static bool IsLoadedInProcess(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm == null || asm.IsDynamic) continue;
                    if (string.Equals(asm.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { /* 个别程序集的 GetName 会抛，忽略 */ }
            }
            return false;
        }

        /// <summary>
        /// 是否 .NET 框架自带 —— 判据是「文件真的在 CLR 运行时目录里」，而不是猜前缀。
        ///
        /// ⚠️ 不要退回"名字以 System./Microsoft. 开头就算框架"的做法，两个方向都会错：
        ///   · <c>SystemManager.dll</c> 不以 "System." 开头，但设备侧真有这个文件；
        ///     用 <c>"System"</c>（不带点）做前缀又会把它误判成框架。
        ///   · <c>System.Memory.dll</c> / <c>System.Threading.Tasks.Extensions.dll</c>
        ///     看着像框架，实际是随设备一起发布的 NuGet 程序集，框架目录里根本没有。
        ///   · <c>Microsoft.Xaml.Behaviors.dll</c> 同理，是真依赖。
        /// 查运行时目录可以把这三类一次性判对。
        /// </summary>
        private static bool IsFrameworkAssembly(string name)
        {
            if (name == "mscorlib" || name == "netstandard" || name == "System") return true;

            try
            {
                // WPF 那几个（PresentationCore / PresentationFramework / System.Xaml /
                // UIAutomation* / WindowsBase）在运行时目录的 WPF 子目录下
                return File.Exists(Path.Combine(RuntimeDir, name + ".dll"))
                    || File.Exists(Path.Combine(WpfDir, name + ".dll"));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>解析失败的报告：CLR 不会说它去哪找过，我们替它说</summary>
        private string BuildMissReport(string simpleName)
        {
            var paths = _searchPaths.ToArray();
            var sb = new StringBuilder();
            sb.Append($"✗ 依赖缺失 {simpleName}.dll —— {paths.Length} 个搜索路径里都没有这个文件");
            foreach (var dir in paths)
                sb.Append($"\n      · {dir}{(Directory.Exists(dir) ? "" : "    (目录不存在)")}");
            return sb.ToString();
        }

        private void ReportOnce(string key, string message)
        {
            lock (_reportGate)
            {
                if (!_reported.Add(key)) return;
            }
            try { OnDiagnostic?.Invoke(message); }
            catch { /* 诊断输出失败不应影响解析 */ }
        }

        /// <summary>把解析失败写入 %TEMP%\MotionApiTester\resolve-errors.log（供事后排查）</summary>
        internal static void LogResolveError(string assemblyName, Exception ex)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "MotionApiTester");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "resolve-errors.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {assemblyName}: {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { /* 日志失败不应影响解析 */ }
        }
    }

    /// <summary>
    /// 依赖体检结果。分成四桶而不是只返回"缺哪些"，是因为
    /// 「磁盘上有文件」和「能解析」不是一回事 —— 纯嵌入（Costura）项目的依赖
    /// 磁盘上永远没有文件，但一个都不缺。
    /// </summary>
    public sealed class DependencyAudit
    {
        public DependencyAudit(
            IReadOnlyList<string> resolved,
            IReadOnlyList<string> fromEmbedded,
            IReadOnlyList<string> resolvedOnDemand,
            IReadOnlyList<string> missing)
        {
            Resolved = resolved;
            FromEmbedded = fromEmbedded;
            ResolvedOnDemand = resolvedOnDemand;
            Missing = missing;
        }

        /// <summary>在进程内 / 搜索路径里找到文件的</summary>
        public IReadOnlyList<string> Resolved { get; }

        /// <summary>由宿主程序集内嵌的 Costura 资源供出的（磁盘上没有对应文件）</summary>
        public IReadOnlyList<string> FromEmbedded { get; }

        /// <summary>
        /// 前三条都没命中、但真的请 CLR 解析一次却成功的。
        /// 有这一桶就说明"靠登记标志推断可用性"是不可靠的 —— 解析链的兜底途径
        /// （比如宿主 &lt;Module&gt; 静态构造自动挂上的 Costura 解析器）不在我们的视野里。
        /// </summary>
        public IReadOnlyList<string> ResolvedOnDemand { get; }

        /// <summary>四条途径全部落空的 —— 这才是真的缺</summary>
        public IReadOnlyList<string> Missing { get; }

        /// <summary>被检查到的非框架引用总数</summary>
        public int Total => Resolved.Count + FromEmbedded.Count + ResolvedOnDemand.Count + Missing.Count;

        /// <summary>一行摘要，供日志/状态栏直接使用</summary>
        public string Summary
        {
            get
            {
                var parts = new List<string> { $"直接可见 {Resolved.Count}" };
                if (FromEmbedded.Count > 0) parts.Add($"内嵌资源 {FromEmbedded.Count}");
                if (ResolvedOnDemand.Count > 0) parts.Add($"按需解析 {ResolvedOnDemand.Count}");
                return string.Join(" · ", parts);
            }
        }
    }
}
