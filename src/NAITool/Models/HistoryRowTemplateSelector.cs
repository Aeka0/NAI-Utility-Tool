using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NAITool;

public sealed class HistoryRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SeparatorTemplate { get; set; }
    public DataTemplate? RowTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) =>
        item is HistoryRow { IsSeparator: true }
            ? SeparatorTemplate ?? base.SelectTemplateCore(item)
            : RowTemplate ?? base.SelectTemplateCore(item);

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
