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
        /// <summary>加载设备 DLL 集合（BaseTester + Model DLL）</summary>
        public AssemblyLoadResult LoadDeviceDlls(string baseTesterPath, string modelDllPath)
        {
            var result = new AssemblyLoadResult();

            // 先加载 BaseTester
            if (File.Exists(baseTesterPath))
            {
                result.BaseTester = LoadAssembly(baseTesterPath);
            }

            // 再加载机型 DLL（触发 Costura 展开）
            if (File.Exists(modelDllPath))
            {
                result.ModelDll = LoadAssembly(modelDllPath);
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

                // Costura 检测
                info.IsCostura = asm.GetTypes().Any(t => t.Namespace == "Costura");

                // 统计嵌入资源
                if (info.IsCostura)
                {
                    try
                    {
                        info.EmbeddedCount = asm.GetManifestResourceNames()
                            .Count(n => n.StartsWith("costura.", StringComparison.OrdinalIgnoreCase));
                    }
                    catch { }
                }
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
