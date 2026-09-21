namespace MotionApiTester.ViewModels
{
    public partial class MainViewModel
    {
        /// <summary>是否深色主题(当前生效值,只读;由 ThemeManager 回调更新)</summary>
        public bool IsDarkTheme => _isDarkTheme;

        /// <summary>主题模式:Light / Dark / System。赋值即应用并持久化。</summary>
        public string ThemeMode
        {
            get => _themeMode;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "Light" : value;
                if (!SetProperty(ref _themeMode, normalized)) return;

                _themeManager.Apply(normalized);
                _settingsService.Settings.Theme = normalized;
                _settingsService.Save();
            }
        }
    }
}
