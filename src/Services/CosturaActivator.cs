using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MotionApiTester.Services
{
    /// <summary>
    /// 激活程序集内嵌的 Costura 依赖解析器。
    ///
    /// <para><b>为什么必须主动激活：</b></para>
    /// Costura/Fody 把被合并的依赖（CustomCore、Core、Prism 系列……）压缩后塞进宿主 DLL 的
    /// 资源表，并以 <c>costura.*.dll.compressed</c> 命名；真正的解析逻辑在
    /// <c>Costura.AssemblyLoader.ResolveAssembly</c>，它靠 <c>Attach()</c> 挂到
    /// <c>AppDomain.CurrentDomain.AssemblyResolve</c> 上。
    ///
    /// <para><b>而这个 Attach() 由宿主程序集的 <c>&lt;Module&gt;::.cctor</c> 调用 ——
    /// 模块静态构造只在「该程序集的代码第一次被执行」时才运行。</b></para>
    ///
    /// <para>本工具用 <c>Assembly.Load(byte[])</c> 加载设备 DLL，之后全程只做元数据反射
    /// （<c>GetTypes()</c> / <c>GetMembers()</c>），<b>从不执行设备侧的任何代码</b>。
    /// 于是模块静态构造永远不触发、Costura 解析器永远不注册，
    /// 那几十个嵌入资源就只是躺在资源表里的压缩字节 —— 一个都用不上。
    /// 此时 CLR 找不到 <c>CustomCore</c> 只能去文件系统翻，而 byte[] 加载的程序集
    /// <c>Location</c> 为空，连「同目录」都无从谈起，最终抛 <c>FileNotFoundException</c>。</para>
    ///
    /// <para>结论：只要在加载后主动反射调用一次 <c>Attach()</c>，
    /// 整棵依赖树就能从嵌入资源按需解出，设备目录不必再平铺那一堆 DLL。</para>
    ///
    /// <para>本类对非嵌入包（老机型 DLL、普通类库）静默跳过，
    /// 由 <see cref="DependencyResolver"/> 的文件系统解析兜底。</para>
    /// </summary>
    public static class CosturaActivator
    {
        private const string LoaderTypeName = "Costura.AssemblyLoader";
        private const string ResourcePrefix = "costura.";
        private const string CompressedSuffix = ".dll.compressed";

        /// <summary>该程序集是否是 Costura 嵌入包（注入了 Costura.AssemblyLoader 类型）</summary>
        public static bool IsCosturaPack(Assembly assembly)
        {
            if (assembly == null) return false;
            try
            {
                // 用 GetType 而不是 GetTypes：只查这一个类型，不触发整程序集的依赖解析，
                // 缺依赖时也不会抛 ReflectionTypeLoadException
                return assembly.GetType(LoaderTypeName, throwOnError: false) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>统计 <c>costura.*</c> 前缀的嵌入资源个数</summary>
        public static int CountEmbeddedResources(Assembly assembly)
        {
            if (assembly == null) return 0;
            try
            {
                return assembly.GetManifestResourceNames()
                               .Count(n => n.StartsWith("costura.", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 列出该宿主内嵌的**程序集简名**（不是资源名）。
        ///
        /// <para>Costura 的资源命名规则是 <c>costura.&lt;小写程序集名&gt;.dll.compressed</c>
        /// （未压缩版为 <c>costura.&lt;名&gt;.dll</c>，符号为 <c>costura.&lt;名&gt;.pdb.compressed</c>），
        /// 这里剥掉前后缀还原成简名，供依赖体检判断"这个引用其实能解出来"。</para>
        ///
        /// <para>只读资源名、不执行任何代码，所以在 Costura 尚未激活时也能安全调用。</para>
        /// </summary>
        public static IReadOnlyList<string> GetEmbeddedAssemblyNames(Assembly assembly)
        {
            var names = new List<string>();
            if (assembly == null) return names;

            try
            {
                foreach (var resource in assembly.GetManifestResourceNames())
                {
                    if (!resource.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                    var body = resource.Substring(ResourcePrefix.Length);

                    // ⚠️ 必须先判 .dll.compressed 再判 .dll，否则 "x.dll.compressed" 剥完剩 "x.dll"
                    string simple;
                    if (body.EndsWith(CompressedSuffix, StringComparison.OrdinalIgnoreCase))
                        simple = body.Substring(0, body.Length - CompressedSuffix.Length);
                    else if (body.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        simple = body.Substring(0, body.Length - ".dll".Length);
                    else
                        continue;   // .pdb / .xml 之类的符号资源，不是程序集

                    if (simple.Length == 0) continue;
                    if (!names.Contains(simple, StringComparer.OrdinalIgnoreCase)) names.Add(simple);
                }
            }
            catch
            {
                // 资源表读不到就当作"没有内嵌" —— 体检会走文件系统那条路，不会因此中断
            }

            return names;
        }

        /// <summary>
        /// 若 <paramref name="assembly"/> 内嵌了 Costura，则激活其依赖解析器。
        /// 非嵌入包、或激活过程出任何问题，都返回 false 并保持原有（文件系统解析）行为。
        /// </summary>
        /// <param name="assembly">已加载的宿主程序集（通常是机型 DLL）</param>
        /// <param name="log">可选诊断出口，任意线程可调</param>
        public static bool TryActivate(Assembly assembly, Action<string> log)
        {
            if (assembly == null) return false;

            if (!IsCosturaPack(assembly))
            {
                // 正常情况：老机型 DLL / 普通类库没有内嵌依赖，交给文件系统解析
                return false;
            }

            string name = SafeName(assembly);
            try
            {
                var loader = assembly.GetType(LoaderTypeName, throwOnError: false);
                if (loader == null) return false;

                // Costura 生成的签名是 Attach(Boolean subscribe)
                var attach = loader
                    .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "Attach" && m.GetParameters().Length <= 1);

                if (attach == null)
                {
                    SafeLog(log, $"⚠️ {name} 检测到 Costura 但没有 Attach 方法，改用文件系统解析依赖");
                    return false;
                }

                var ps = attach.GetParameters();
                object[] argv = null;
                if (ps.Length == 1)
                {
                    argv = new object[]
                    {
                        ps[0].HasDefaultValue ? ps[0].DefaultValue : DefaultFor(ps[0].ParameterType)
                    };
                }

                attach.Invoke(null, argv);

                SafeLog(log, $"✓ 已激活 {name} 内嵌的 Costura 依赖解析器" +
                             $"（{CountEmbeddedResources(assembly)} 个嵌入资源，依赖不再需要外放）");
                return true;
            }
            catch (Exception ex)
            {
                SafeLog(log, $"⚠️ 激活 {name} 的 Costura 解析器失败（回退文件系统解析）: " +
                             $"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static object DefaultFor(Type t) => t == typeof(bool);

        private static string SafeName(Assembly a)
        {
            try { return a.GetName().Name ?? "(未知程序集)"; }
            catch { return "(未知程序集)"; }
        }

        private static void SafeLog(Action<string> log, string message)
        {
            if (log == null) return;
            try { log(message); }
            catch { /* 诊断出口不允许影响主流程 */ }
        }
    }
}
