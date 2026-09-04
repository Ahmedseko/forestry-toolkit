using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TreeCounterAddin
{
    // Backs the tab strip in TreeCounterDockpaneView.xaml (ListBox/SelectedTabIndex replacing
    // the old TabControl, see the Esri Community feedback noted there): each tab's content
    // panel is Visible only when SelectedTabIndex equals its own ConverterParameter index.
    public class IndexToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var selected = value is int i ? i : -1;
            var target = parameter is string s && int.TryParse(s, out var p) ? p : -1;
            return selected == target ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
