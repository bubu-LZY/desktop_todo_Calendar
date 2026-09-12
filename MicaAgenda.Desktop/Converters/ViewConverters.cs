using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MicaAgenda.App.ViewModels;

namespace MicaAgenda.Desktop.Converters;

/// <summary>
/// WPF 版的 DataTrigger 视觉状态（今日/选中/非本月、重要/完成、节假日）在 Avalonia
/// 里没有等价机制，改为"状态 → 画刷/透明度/边框"的一等值转换器。
/// 颜色常量与 WPF 版 MainWindow.xaml 的资源字典保持一致。
/// </summary>
public static class CellPalette
{
    public static readonly IBrush DayCell = Brush("#AFFFFFFF");
    public static readonly IBrush DayCellOutMonth = Brush("#6FFFFFFF");
    public static readonly IBrush TodayCell = Brush("#BFDCEBFE");
    public static readonly IBrush SelectedCell = Brush("#662563EB");
    public static readonly IBrush CellBorder = Brush("#18000000");
    public static readonly IBrush TodayBorder = Brush("#993B82F6");
    public static readonly IBrush SelectedBorder = Brush("#FF2563EB");
    public static readonly IBrush PrimaryText = Brush("#111827");
    public static readonly IBrush MutedText = Brush("#6B7280");

    public static readonly IBrush TaskPill = Brush("#BAE9F7EF");
    public static readonly IBrush TaskPillImportant = Brush("#FECACA");
    public static readonly IBrush TaskBorder = Brush("#33000000");
    public static readonly IBrush TaskBorderImportant = Brush("#EF4444");
    public static readonly IBrush ImportantText = Brush("#B91C1C");

    public static readonly IBrush HolidayBreak = Brush("#FEE2E2");
    public static readonly IBrush HolidayWork = Brush("#DBEAFE");
    public static readonly IBrush HolidayText = Brush("#991B1B");
    public static readonly IBrush HolidayWorkText = Brush("#1D4ED8");

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}

public sealed class DayCellBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not DayCellViewModel vm ? CellPalette.DayCell
            : vm.IsSelected ? CellPalette.SelectedCell
            : vm.IsToday ? CellPalette.TodayCell
            : vm.IsInCurrentMonth ? CellPalette.DayCell
            : CellPalette.DayCellOutMonth;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DayCellBorderBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not DayCellViewModel vm ? CellPalette.CellBorder
            : vm.IsSelected ? CellPalette.SelectedBorder
            : vm.IsToday ? CellPalette.TodayBorder
            : CellPalette.CellBorder;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DayCellBorderThicknessConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not DayCellViewModel vm ? new Avalonia.Thickness(0.5)
            : vm.IsSelected ? new Avalonia.Thickness(1.6)
            : vm.IsToday ? new Avalonia.Thickness(1.2)
            : new Avalonia.Thickness(0.5);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DayCellOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not DayCellViewModel vm ? 1.0
            : (vm.IsInCurrentMonth || vm.IsToday || vm.IsSelected) ? 1.0 : 0.46;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TaskPillBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? CellPalette.TaskPillImportant : CellPalette.TaskPill;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TaskPillBorderConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? CellPalette.TaskBorderImportant : CellPalette.TaskBorder;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TaskTitleForegroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? CellPalette.ImportantText : CellPalette.PrimaryText;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TaskTitleOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 0.45 : 1.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class HolidayBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? CellPalette.HolidayWork : CellPalette.HolidayBreak;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class HolidayForegroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? CellPalette.HolidayWorkText : CellPalette.HolidayText;

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