using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace UEBulkExport.Gui.Localization;

/// <summary>
/// <c>{loc:Tr Some.Key}</c> in XAML: a one-way binding to the string table that refreshes when
/// the language changes. Deliberately a reflection binding - it does not depend on the view's
/// data type, so it works in styles, templates and windows alike.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
