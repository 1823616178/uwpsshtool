using System;
using System.ComponentModel;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;

namespace SshTool.App.Controls
{
    public sealed partial class TunnelRow : UserControl
    {
        private TunnelItemViewModel _vm;
        private bool _suppressToggle;
        private bool _ignoreNextTap;

        public TunnelRow()
        {
            this.InitializeComponent();
            this.DataContextChanged += OnDataContextChanged;
            this.Unloaded += OnUnloaded;
        }

        public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
            nameof(IsCompact), typeof(bool), typeof(TunnelRow),
            new PropertyMetadata(false, OnIsCompactChanged));

        public bool IsCompact
        {
            get { return (bool)GetValue(IsCompactProperty); }
            set { SetValue(IsCompactProperty, value); }
        }

        private static void OnIsCompactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var tunnelRow = (TunnelRow)d;
            if (tunnelRow.RowCtl != null)
            {
                tunnelRow.RowCtl.IsCompact = (bool)e.NewValue;
            }
        }

        public TunnelItemViewModel ViewModel => _vm;

        public event EventHandler EditRequested;
        public event EventHandler ToggleRequested;
        public event EventHandler DeleteRequested;

        private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _vm = args.NewValue as TunnelItemViewModel;
            if (_vm != null)
            {
                _vm.PropertyChanged += OnViewModelPropertyChanged;
            }
            BindRow();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            }
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            BindRow();
        }

        private void BindRow()
        {
            TunnelItemViewModel vm = _vm;
            if (vm == null)
            {
                return;
            }

            RowCtl.Title = vm.Name;
            RowCtl.Subtitle = vm.Subtitle;
            RowCtl.IsCompact = IsCompact;
            Dot.State = vm.StatusDotState;

            _suppressToggle = true;
            SwitchCtl.IsEnabled = vm.CanToggle;
            SwitchCtl.IsOn = vm.IsRunning;
            _suppressToggle = false;

            if (MenuToggleItem != null)
            {
                MenuToggleItem.IsEnabled = vm.CanToggle;
                MenuToggleItem.Text = vm.IsRunning
                    ? (Localized.Get("Tunnels_MenuStop", "停止"))
                    : (Localized.Get("Tunnels_MenuStart", "启动"));
            }
        }

        private void OnSwitchToggled(object sender, RoutedEventArgs e)
        {
            if (_suppressToggle)
            {
                return;
            }
            Raise(ToggleRequested);
        }

        private void OnRowClick(object sender, EventArgs e)
        {
            if (_ignoreNextTap)
            {
                _ignoreNextTap = false;
                return;
            }
            Raise(EditRequested);
        }

        private void OnHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started)
            {
                return;
            }
            ShowMenu();
            _ignoreNextTap = true;
            e.Handled = true;
        }

        private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            try
            {
                RowMenu.ShowAt(this, e.GetPosition(this));
            }
            catch (Exception)
            {
                ShowMenu();
            }
            _ignoreNextTap = true;
            e.Handled = true;
        }

        private void ShowMenu()
        {
            FlyoutBase.SetAttachedFlyout(this, RowMenu);
            FlyoutBase.ShowAttachedFlyout(this);
        }

        private void OnMenuToggleClick(object sender, RoutedEventArgs e)
        {
            Raise(ToggleRequested);
        }

        private void OnMenuEditClick(object sender, RoutedEventArgs e)
        {
            Raise(EditRequested);
        }

        private void OnMenuDeleteClick(object sender, RoutedEventArgs e)
        {
            Raise(DeleteRequested);
        }

        private void Raise(EventHandler handler)
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
    }
}
