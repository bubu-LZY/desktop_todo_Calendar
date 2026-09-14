using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MicaAgenda.Desktop.Converters;

// 说明：日期格子 / 任务胶囊 / 节假日徽标的颜色原先由这里的「状态 → 画刷」转换器决定，
// 但转换器的结果会被 Avalonia 缓存，切换背景模式时刷不动（表现就是不管什么模式都是纯白底）。
// 现在颜色统一改由 MainWindow.ApplyBackgroundResources() 写资源 + XAML 里的
// {DynamicResource} / Classes.xxx="{Binding}" 条件样式决定，这里只保留与配色无关的转换器。

/// <summary>已完成任务标题降透明度（未完成返回 1.0）。</summary>
public sealed class TaskTitleOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 0.45 : 1.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把视图模式枚举和参数字符串比较，相等返回 true —— 用于三个视图容器的 IsVisible 切换。</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && string.Equals(value.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔取反，用于"编辑态隐藏普通标题/显示输入框"这类互斥可见性。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>已完成任务标题加删除线（未完成返回 null 即无装饰）。</summary>
public sealed class CompletedStrikethroughConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TextDecorations.Strikethrough : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>DateOnly → "M/d" 短日期，用于右侧本周任务行的日期列。
/// 不走 StringFormat 是为了避开 Avalonia 对 "{}{0:...}" 前导花括号转义的解析歧义。</summary>
public sealed class ShortDateConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateOnly d ? d.ToString("M/d", culture) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
