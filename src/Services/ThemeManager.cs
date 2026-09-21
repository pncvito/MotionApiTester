using System;
using System.Windows;

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

        /// <summary>
        /// 应用主题。字典挂在 <see cref="Application.Resources"/> 上（**不是** MainWindow.Resources）。
        ///
        /// <para>为什么必须挂应用级：设置窗口 / 设备向导都是独立的 Window，
        /// 窗口级字典照不到它们 —— 这也正是那两个窗口当初只能硬编码颜色的原因：
        /// 深色模式下它们完全不跟主题。挂到应用级后所有窗口一起生效，
        /// 那两个窗口里的 <c>{DynamicResource XxxBrush}</c> 才有东西可解析。</para>
        /// </summary>
        public void Apply(string themeMode)
        {
            UpdateIsDark(themeMode);

            var app = Application.Current;
            if (app == null) return;

            SwapThemeDictionary(app.Resources, new ResourceDictionary
            {
                Source = new Uri(IsDark ? DarkDictionary : LightDictionary, UriKind.Relative)
            });

            // 历史版本把字典挂在主窗口上；窗口级优先于应用级，不清掉会盖住新主题
            if (app.MainWindow != null) SwapThemeDictionary(app.MainWindow.Resources, null);
        }

        /// <summary>移除资源里的主题字典，再追加 replacement（null 表示只移除）</summary>
        private static void SwapThemeDictionary(ResourceDictionary target, ResourceDictionary replacement)
        {
            var merged = target.MergedDictionaries;
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                var source = merged[i].Source?.ToString();
                if (source != null && (source.Contains("LightTheme") || source.Contains("DarkTheme")))
                    merged.RemoveAt(i);
            }

            if (replacement != null) merged.Add(replacement);
        }

        /// <summary>
        /// 保留此方法名只是为了不改调用点。
        /// 字典已挂到应用级资源上，主题与主窗口的创建顺序不再相关（Application.Resources
        /// 在 App 初始化时就绪，早于 StartupUri 创建主窗口），因此直接应用即可。
        /// </summary>
        public void ApplyDeferred(string themeMode) => Apply(themeMode);

        private void UpdateIsDark(string themeMode)
        {
            IsDark = ResolveIsDark(themeMode);
            _onIsDarkChanged?.Invoke(IsDark);
        }
    }
}
