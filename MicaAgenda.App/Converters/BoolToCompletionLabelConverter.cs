using System.Globalization;
using System.Windows.Data;

namespace MicaAgenda.App.Converters;

/// <summary>
/// 把"是否已完成"布尔值转为面板上的分组标题文字。
/// true → 已完成；false → 未完成。
/// 用于 CollectionViewSource 的 PropertyGroupDescription 后的 GroupStyle HeaderTemplate。
/// </summary>
public sealed class BoolToCompletionLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? "已完成" : "未完成";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
