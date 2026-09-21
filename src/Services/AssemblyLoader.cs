using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MotionApiTester.Models;

namespace MotionApiTester.Services
{
    /// <summary>DLL 程序集加载器（含 Costura 检测）</summary>
    public class AssemblyLoader
    {
        /// <summary>
        /// 加载设备 DLL 集合（BaseTester + 机型 DLL）。
        ///
        /// <para>⚠️ 顺序不可颠倒：机型 DLL 必须先加载、并<b>显式激活</b>其内嵌的 Costura 解析器，
        /// 再加载 BaseTester。因为 BaseTester 并不自带依赖，它那 12 个外部引用
        /// （CustomCore / Core / Prism 系列……）要靠机型 DLL 的嵌入资源供给，
        /// 而那个解析器只有被主动 Attach 过才会生效。</para>
        ///
        /// <para>注意：<c>LoadFrom</c> 本身<b>不会</b>触发 Costura 展开 ——
        /// 展开（Attach）发生在宿主程序集的模块静态构造里，只有宿主代码被执行时才跑。</para>
        /// </summary>
        public AssemblyLoadResult LoadDeviceDlls(string baseTesterPath, string modelDllPath)
        {
            var result = new AssemblyLoadResult();

            // ① 先加载机型 DLL 并激活它内嵌的 Costura 解析器
            if (File.Exists(modelDllPath))
            {
                result.ModelDll = LoadAssembly(modelDllPath);
                try { CosturaActivator.TryActivate(Assembly.LoadFrom(modelDllPath), null); }
                catch { /* 激活失败则回退文件系统解析 */ }
            }

            // ② 再加载 BaseTester —— 此时它的依赖已可由①的嵌入资源解出
            if (File.Exists(baseTesterPath))
            {
                result.BaseTester = LoadAssembly(baseTesterPath);
            }

            return result;
        }

        /// <summary>加载单个程序集</summary>
        public ApiAssembly LoadAssembly(string path)
        {
            var info = new ApiAssembly { Path = path };

            try
            {
                var asm = Assembly.LoadFrom(path);
                info.Name = asm.GetName().Name ?? Path.GetFileNameWithoutExtension(path);
                info.Version = asm.GetName().Version?.ToString();

                // Costura 检测（单查类型，避免 GetTypes() 在缺依赖时抛异常）
                info.IsCostura = CosturaActivator.IsCosturaPack(asm);
                if (info.IsCostura)
                    info.EmbeddedCount = CosturaActivator.CountEmbeddedResources(asm);
            }
            catch (BadImageFormatException)
            {
                info.LoadErrors.Add("原生 C++ DLL，不支持 .NET 反射");
            }
            catch (FileNotFoundException ex)
            {
                info.LoadErrors.Add($"缺少依赖: {ex.FileName}");
            }
            catch (Exception ex)
            {
                info.LoadErrors.Add($"{ex.GetType().Name}: {ex.Message}");
            }

            return info;
        }
    }

    public class AssemblyLoadResult
    {
        public ApiAssembly BaseTester { get; set; }
        public ApiAssembly ModelDll { get; set; }
        public bool Success => BaseTester != null && ModelDll != null
                           && !BaseTester.LoadErrors.Any() && !ModelDll.LoadErrors.Any();
    }
}
