using System;
using System.IO;
using System.Linq;

namespace MotionApiTester.Services
{
    /// <summary>
    /// 设备 DLL 目录解析。
    /// 目的：不把开发机的绝对路径写死在代码里。
    /// 优先使用 settings.json 的 DefaultDeviceDirectory；为空或目录已失效时，
    /// 按 exe 的相对位置探测常见仓库布局，最后退回 exe 所在目录。
    /// </summary>
    public static class DeviceDirectoryResolver
    {
        /// <summary>相对 exe 目录的候选布局（按可信度排序）</summary>
        private static readonly string[] Candidates =
        {
            "Bin",              // &lt;exe&gt;\Bin —— 已按部署布局放置
            @"..\..\..\Bin",    // src\bin\Debug → 仓库根\Bin（当前源码树布局）
            @"..\..\Bin",       // &lt;exe 上两级&gt;\Bin
            @"..\Bin"           // &lt;exe 上一级&gt;\Bin
        };

        /// <summary>
        /// 解析生效的设备 DLL 目录。
        /// <paramref name="configured"/> 已存在则直接采用；否则探测；探测失败退回 exe 目录。
        /// </summary>
        public static string Resolve(string configured)
        {
            var normalized = Normalize(configured);

            // 显式配置且目录存在 → 尊重用户设置（即使里面暂时没有 DLL）
            if (normalized != null && Directory.Exists(normalized))
                return normalized;

            return Probe() ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>去掉首尾空白与成对引号；空值返回 null</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return path.Trim().Trim('"');
        }

        /// <summary>按候选布局探测第一个含 *.dll 的目录；找不到返回 null</summary>
        private static string Probe()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var relative in Candidates)
            {
                var dir = SafeFullPath(baseDir, relative);
                if (dir != null && HasDlls(dir))
                    return dir;
            }
            return null;
        }

        private static string SafeFullPath(string baseDir, string relative)
        {
            try { return Path.GetFullPath(Path.Combine(baseDir, relative)); }
            catch { return null; }
        }

        private static bool HasDlls(string dir)
        {
            try
            {
                return Directory.Exists(dir)
                       && Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly).Any();
            }
            catch { return false; }
        }
    }
}
