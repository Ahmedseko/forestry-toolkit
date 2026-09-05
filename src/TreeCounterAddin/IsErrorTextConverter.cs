using System;
using System.Globalization;
using System.Windows.Data;

namespace TreeCounterAddin
{
    // Backs the StatusLine style's error-color DataTrigger in TreeCounterDockpaneView.xaml -
    // every feature's own "XxxStatus" property is a plain bilingual string set from its
    // Tr(...) call site (see e.g. TreeCounterDockpaneViewModel.RiparianBuffer.cs), not a
    // separate IsError flag, so this just sniffs the message text itself for the
    // English/Indonesian failure prefixes those call sites already use consistently
    // ("Failed to..."/"Gagal...", "Unexpected error"/"Error tak terduga") rather than adding
    // a parallel bool property to every feature.
    public class IsErrorTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string s || string.IsNullOrEmpty(s))
                return false;
            return s.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("gagal", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
