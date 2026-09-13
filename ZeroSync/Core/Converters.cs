using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ZeroSync.Core
{
    /// <summary>
    /// Đảo ngược bool: true → false, false → true
    /// </summary>
    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : value;
    }

    /// <summary>
    /// Watch status → Background brush: true = green, false = gray
    /// </summary>
    public class WatchStatusBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var isWatching = value is bool b && b;
            return isWatching
                ? (Brush)Application.Current.FindResource("AccentGreenBrush")
                : (Brush)new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Watch status → Text: true = "WATCHING", false = "IDLE"
    /// </summary>
    public class WatchStatusTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? "👁 WATCHING" : "IDLE";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>bool → Visibility: true = Visible, false = Collapsed</summary>
    public class BoolToVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Phân tích dòng log để trả về màu sắc tương ứng
    /// </summary>
    public class LogEntryColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is string log) || string.IsNullOrEmpty(log))
                return Application.Current.FindResource("TextPrimaryBrush");

            var lower = log.ToLower();

            if (lower.Contains("error") || lower.Contains("fail") || lower.Contains("lỗi") || lower.Contains("exception") || lower.Contains("✖") || lower.Contains("thất bại"))
                return Application.Current.FindResource("AccentRedBrush");

            if (lower.Contains("warn") || lower.Contains("cảnh báo") || lower.Contains("warning") || lower.Contains("⚠"))
                return Application.Current.FindResource("AccentOrangeBrush");

            if (lower.Contains("success") || lower.Contains("thành công") || lower.Contains("✔") || lower.Contains("cài xong") || lower.Contains("lưu xong") || lower.Contains("đã lưu"))
                return Application.Current.FindResource("AccentGreenBrush");

            if (lower.Contains("watch") || lower.Contains("theo dõi") || lower.Contains("👁") || lower.Contains("phát hiện") || lower.Contains("triggered"))
                return Application.Current.FindResource("AccentBlueBrush");

            return Application.Current.FindResource("TextPrimaryBrush");
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>bool → Visibility đảo ngược: true = Collapsed, false = Visible</summary>
    public class InverseBoolToVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
