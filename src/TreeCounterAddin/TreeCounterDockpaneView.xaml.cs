using System.ComponentModel;
using System.Windows.Controls;

namespace TreeCounterAddin
{
    public partial class TreeCounterDockpaneView : UserControl
    {
        private bool _updatingFromViewModel;

        public TreeCounterDockpaneView()
        {
            InitializeComponent();
            DataContextChanged += (_, e) =>
            {
                if (e.OldValue is TreeCounterDockpaneViewModel oldVm)
                    oldVm.PropertyChanged -= ViewModel_PropertyChanged;
                if (e.NewValue is TreeCounterDockpaneViewModel newVm)
                {
                    newVm.PropertyChanged += ViewModel_PropertyChanged;

                    // The ViewModel loads a saved key straight into its backing field in
                    // its constructor (bypassing the property setter, so no PropertyChanged
                    // fires for it) - without this, the box stays empty until the user types
                    // something or switches providers, even though the key was restored fine
                    // internally. Push the already-loaded value in now.
                    _updatingFromViewModel = true;
                    ApiKeyBox.Password = newVm.ApiKey ?? "";
                    FirmsMapKeyBox.Password = newVm.FirmsMapKey ?? "";
                    _updatingFromViewModel = false;
                }
            };
        }

        // PasswordBox.Password isn't a DependencyProperty, so it can't be data-bound
        // directly - wire it to the ViewModel by hand instead of showing the API key
        // in plaintext in a regular TextBox.
        private void ApiKeyBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_updatingFromViewModel) return;
            if (DataContext is TreeCounterDockpaneViewModel vm)
                vm.ApiKey = ApiKeyBox.Password;
        }

        // Same reasoning as ApiKeyBox_PasswordChanged above, for the NASA FIRMS MAP_KEY.
        private void FirmsMapKeyBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_updatingFromViewModel) return;
            if (DataContext is TreeCounterDockpaneViewModel vm)
                vm.FirmsMapKey = FirmsMapKeyBox.Password;
        }

        // The ViewModel also writes ApiKey/FirmsMapKey itself (e.g. restoring a per-
        // provider saved key when SelectedProvider changes) - since PasswordBoxes can't be
        // bound, that write needs to be pushed into the box by hand too, or the box would
        // still show the old value even though the ViewModel already moved on.
        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (DataContext is not TreeCounterDockpaneViewModel vm) return;

            if (e.PropertyName == nameof(TreeCounterDockpaneViewModel.ApiKey) && ApiKeyBox.Password != vm.ApiKey)
            {
                _updatingFromViewModel = true;
                ApiKeyBox.Password = vm.ApiKey ?? "";
                _updatingFromViewModel = false;
            }
            else if (e.PropertyName == nameof(TreeCounterDockpaneViewModel.FirmsMapKey) && FirmsMapKeyBox.Password != vm.FirmsMapKey)
            {
                _updatingFromViewModel = true;
                FirmsMapKeyBox.Password = vm.FirmsMapKey ?? "";
                _updatingFromViewModel = false;
            }
        }
    }
}
