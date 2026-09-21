using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MotionApiTester.ViewModels
{
    // 只保留 MainWindow.xaml 里真正 StaticResource 引用的转换器。
    // 之前堆了 10 个，其中 7 个从未被任何 XAML 引用（图标 / 枚举 / 颜色类）——
    // 死代码会让人误以为界面某处依赖它，改起来束手束脚，已清掉。
    // 注：布尔→Visibility 用的是 WPF 自带 BooleanToVisibilityConverter（在 MainWindow.xaml 里声明）。

    /// <summary>Null → Visibility 转换器（null=Visible, not-null=Collapsed）</summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool inverted = parameter?.ToString() == "Inverted";
            bool isNull = value == null;
            bool visible = inverted ? !isNull : isNull;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>反向布尔转换器（true→false, false→true）</summary>
    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : value;
    }

    /// <summary>反向布尔 → Visibility（false = Visible, true = Collapsed）</summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool flag = value is bool b && b;
            return flag ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

}