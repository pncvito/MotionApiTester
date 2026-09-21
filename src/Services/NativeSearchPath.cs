using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MotionApiTester.Services
{
    /// <summary>
    /// 把设备目录挂进 Windows 的原生 DLL 搜索路径。
    ///
    /// <para><b>为什么必须做：</b></para>
    /// 托管程序集靠 <c>AppDomain.AssemblyResolve</c> 解析，
    /// 而设备侧托管 DLL 里的 <c>[DllImport("LTSMC.dll")]</c> 走的是
    /// <b>另一套完全独立</b>的 Windows LoadLibrary 搜索顺序：
    /// <list type="number">
    ///   <item>应用程序（exe）所在目录</item>
    ///   <item>系统目录 <c>C:\Windows\System32</c></item>
    ///   <item>16 位系统目录</item>
    ///   <item>Windows 目录</item>
    ///   <item>当前工作目录</item>
    ///   <item><c>PATH</c> 环境变量中的目录</item>
    /// </list>
    /// 设备目录（如 <c>D:\MotionApiTester\Bin</c>）<b>不在这六项中的任何一项</b>，
    /// 于是即便 LTSMC.dll 就躺在那里，LoadLibrary 依然找不到它，
    /// 抛 <c>DllNotFoundException</c>，HRESULT <c>0x8007007E</c>（<c>ERROR_MOD_NOT_FOUND</c>）。
    ///
    /// <para><b>⚠️ 别被错误码带偏：</b>0x8007007E 常被误读成「LTSMC.dll 缺依赖」。
    /// 实际上只要 LTSMC.dll 的导入表只含系统 DLL
    /// （实测：KERNEL32 / USER32 / GDI32 / MSIMG32 / gdiplus / ole32 …，17 个全是系统库），
    /// 它就是自足的 —— 缺的是<b>路径</b>，不是<b>模块</b>。
    /// 判断方法：<c>dumpbin /dependents LTSMC.dll</c>。</para>
    ///
    /// <para><see cref="SetDllDirectory"/> 会把该目录插入上述顺序
    /// （系统目录之后、当前目录之前），且同样作用于
    /// 「被加载 DLL 自身依赖」的搜索 —— 这正是设备目录里那一堆原生 DLL 需要的。</para>
    /// </summary>
    public static class NativeSearchPath
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string lpPathName);

        /// <summary>
        /// 把设备目录设为进程级原生 DLL 搜索目录。
        /// 传 null / 空串则恢复系统默认顺序。
        /// </summary>
        /// <returns>设置成功返回 true</returns>
        public static bool SetDeviceDirectory(string directory, Action<string> log)
        {
            // 恢复默认（卸载设备时用）
            if (string.IsNullOrWhiteSpace(directory))
            {
                try
                {
                    SetDllDirectory(null);
                    SafeLog(log, "↩️ 已恢复默认的原生 DLL 搜索路径");
                }
                catch (Exception ex)
                {
                    SafeLog(log, $"⚠️ 恢复原生 DLL 搜索路径失败: {ex.Message}");
                }
                return true;
            }

            if (!Directory.Exists(directory))
            {
                SafeLog(log, $"⚠️ 原生 DLL 搜索路径未设置：目录不存在 {directory}");
                return false;
            }

            try
            {
                if (SetDllDirectory(directory))
                {
                    SafeLog(log, $"✓ 已将设备目录加入原生 DLL 搜索路径: {directory}");
                    return true;
                }

                int err = Marshal.GetLastWin32Error();
                SafeLog(log, $"⚠️ SetDllDirectory 失败（Win32 错误 {err}），" +
                             $"设备侧 P/Invoke 可能报 DllNotFoundException");
                return false;
            }
            catch (Exception ex)
            {
                SafeLog(log, $"⚠️ SetDllDirectory 异常: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void SafeLog(Action<string> log, string message)
        {
            if (log == null) return;
            try { log(message); }
            catch { /* 诊断出口不允许影响主流程 */ }
        }
    }
}
