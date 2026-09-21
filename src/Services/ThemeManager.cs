using System;
using System.Windows;
using System.Windows.Threading;

namespace MotionApiTester.Services
{
    /// <summary>
    /// 主题应用：把 AppSettings.Theme（Light / Dark / System）翻译成实际主题字典并替换窗口资源。
    /// 与 ViewModel 解耦，通过回调把"当前是否深色"同步回去。
    /// </summary>
    public class ThemeManager
    {
        private const string LightDictionary = "Themes/LightTheme.xaml";
        private const string DarkDictionary = "Themes/DarkTheme.xaml";

        private readonly Action<bool> _onIsDarkChanged;

        /// <param name="onIsDarkChanged">主题切换生效后的回调，参数为"是否深色"</param>
        public ThemeManager(Action<bool> onIsDarkChanged = null)
        {
            _onIsDarkChanged = onIsDarkChanged;
        }

        /// <summary>当前生效的是否深色</summary>
        public bool IsDark { get; private set; }

        /// <summary>把主题模式（Light / Dark / System）解析为是否深色</summary>
        public static bool ResolveIsDark(string themeMode)
        {
            if (string.Equals(themeMode, "Dark", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(themeMode, "Light", StringComparison.OrdinalIgnoreCase)) return false;
            return IsOsDarkTheme();
        }

        /// <summary>读取系统"应用"主题是否为深色</summary>
        public static bool IsOsDarkTheme()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key?.GetValue("AppsUseLightTheme");
                    if (value is int light) return light == 0;
                }
            }
            catch { /* 读不到注册表就按浅色处理 */ }
            return false;
        }

        /// <summary>应用主题。需要 MainWindow 已经创建，否则只更新状态、不动资源字典。</summary>
        public void Apply(string themeMode)
        {
            UpdateIsDark(themeMode);

            var window = Application.Current?.MainWindow;
            if (window == null) return;

            var merged = window.Resources.MergedDictionaries;
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                var source = merged[i].Source?.ToString();
                if (source != null && (source.Contains("LightTheme") || source.Contains("DarkTheme")))
                    merged.RemoveAt(i);
            }

            merged.Add(new ResourceDictionary
            {
                Source = new Uri(IsDark ? DarkDictionary : LightDictionary, UriKind.Relative)
            });
        }

        /// <summary>
        /// 启动时机问题：MainViewModel 由 MainWindow 的 XAML 在窗口构造函数中创建，
        /// 此时 Application.Current.MainWindow 仍为 null，直接应用主题会被静默跳过，
        /// 表现为"设置里存了深色主题但启动还是浅色"。因此延后到窗口 Loaded 之后再应用。
        /// </summary>
        public void ApplyDeferred(string themeMode)
        {
            var app = Application.Current;
            if (app == null)
            {
                // 无 Application（设计时 / 单元测试）—— 只同步状态
                UpdateIsDark(themeMode);
                return;
            }

            if (app.MainWindow == null)
                app.Dispatcher.BeginInvoke(new Action(() => Apply(themeMode)), DispatcherPriority.Loaded);
            else
                Apply(themeMode);
        }

        private void UpdateIsDark(string themeMode)
        {
            IsDark = ResolveIsDark(themeMode);
            _onIsDarkChanged?.Invoke(IsDark);
        }
    }
}
