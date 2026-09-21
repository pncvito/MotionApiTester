using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MotionApiTester.Services
{
    /// <summary>
    /// 程序集依赖解析（AssemblyResolve 的实际逻辑）。
    ///
    /// 典型场景：BinocRainbow 用 Costura 嵌入了 BaseTester，当它内部类型被反射时，
    /// CLR 在 LoadFile 上下文里找不到 BaseTester 的文件路径版 → FileNotFoundException。
    /// 这里按"已登记的字节缓存 → 搜索路径"顺序返回程序集。
    ///
    /// 搜索路径全部在运行时推导，代码里不写死任何开发机绝对路径；
    /// 额外的私有依赖目录请通过 settings.json 的 ExtraDependencySearchPaths 配置。
    /// </summary>
    public class DependencyResolver
    {
        private readonly Dictionary<string, byte[]> _byteCache = new Dictionary<string, byte[]>();
        private readonly List<string> _searchPaths = new List<string>();

        public DependencyResolver()
        {
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

        /// <summary>清空字节缓存（卸载 / 切换设备时调用）</summary>
        public void Clear() => _byteCache.Clear();

        /// <summary>
        /// AssemblyResolve 入口。
        /// 解析不到返回 null，交给 CLR 继续按默认规则查找。
        /// </summary>
        public Assembly Resolve(string assemblyFullName)
        {
            try
            {
                // Name 可能是 "OptoFidelity.BaseTester" 或 "OptoFidelity.BaseTester, Version=..."
                var simpleName = assemblyFullName?.Split(',')[0].Trim();
                if (string.IsNullOrEmpty(simpleName)) return null;

                // 1. 加载时登记的字节缓存
                if (_byteCache.TryGetValue(simpleName, out var bytes))
                    return Assembly.Load(bytes);

                // 2. 依次尝试各搜索路径
                foreach (var dir in _searchPaths.ToArray())
                {
                    if (!Directory.Exists(dir)) continue;

                    var direct = Path.Combine(dir, simpleName + ".dll");
                    if (File.Exists(direct))
                        return Assembly.LoadFrom(direct);

                    // NuGet 布局：包名/版本/lib/netXXX/xxx.dll
                    foreach (var packageDir in Directory.GetDirectories(dir, simpleName + "*", SearchOption.TopDirectoryOnly))
                    {
                        var dll = Directory.GetFiles(packageDir, simpleName + ".dll", SearchOption.AllDirectories).FirstOrDefault();
                        if (dll != null) return Assembly.LoadFrom(dll);
                    }
                }
            }
            catch (Exception ex)
            {
                LogResolveError(assemblyFullName, ex);
            }

            return null;
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
}
