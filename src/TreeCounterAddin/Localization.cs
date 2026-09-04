using System;
using System.Windows.Data;
using System.Windows.Markup;

namespace TreeCounterAddin
{
    // Shorthand markup extensions for the "{Binding IsHelpEnglish, Converter={StaticResource
    // UiText}, ConverterParameter=Key}" pattern used ~170 times across TreeCounterDockpaneView.xaml
    // (feedback from an Esri Community post about this project, 2026-09-05: that binding is a lot
    // of typing per string). {local:Tr Key} / {local:TrTip Key} are just a terser XAML spelling of
    // the exact same binding - still the same IsHelpEnglish flag, still the same
    // UiTextConverter/BilingualTooltipConverter dictionaries (see those files). Deliberately NOT a
    // resx/CultureInfo-based localization framework: language here is a per-viewmodel toggle the
    // user flips on the Help tab, not a process-wide culture, so a MarkupExtension that returns a
    // plain Binding (which needs no static/global language source) fits without adding a satellite
    // assembly or resource-manager layer this app doesn't otherwise need.
    public class TrExtension : MarkupExtension
    {
        private static readonly UiTextConverter Converter = new();

        public string Key { get; set; }

        public TrExtension() { }
        public TrExtension(string key) => Key = key;

        public override object ProvideValue(IServiceProvider serviceProvider) =>
            new Binding(nameof(TreeCounterDockpaneViewModel.IsHelpEnglish))
            {
                Converter = Converter,
                ConverterParameter = Key
            }.ProvideValue(serviceProvider);
    }

    public class TrTipExtension : MarkupExtension
    {
        private static readonly BilingualTooltipConverter Converter = new();

        public string Key { get; set; }

        public TrTipExtension() { }
        public TrTipExtension(string key) => Key = key;

        public override object ProvideValue(IServiceProvider serviceProvider) =>
            new Binding(nameof(TreeCounterDockpaneViewModel.IsHelpEnglish))
            {
                Converter = Converter,
                ConverterParameter = Key
            }.ProvideValue(serviceProvider);
    }
}
