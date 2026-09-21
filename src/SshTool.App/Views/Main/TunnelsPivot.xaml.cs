using System;
using System.ComponentModel;
using System.Threading.Tasks;
using SshTool.App.Controls;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using SshTool.Core.Common;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views.Main
{
    public sealed partial class TunnelsPivot : UserControl
    {
        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();
        private readonly TunnelsViewModel _viewModel;

        public TunnelsPivot()
        {
            _viewModel = new TunnelsViewModel(AppServices.Current);
            this.InitializeComponent();
            this.Loaded += OnLoaded;
            this.Unloaded += OnUnloaded;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        public TunnelsViewModel ViewModel => _viewModel;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Empty.PrimaryCommand = _viewModel.AddCommand;
            TunnelList.ItemsSource = _viewModel.Tunnels;
            _viewModel.LoadAsync().Forget("TunnelsPivot.Load", AppLog.Logger);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _viewModel.Detach();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TunnelsViewModel.IsEmpty))
            {
                UpdateEmptyVisibility();
            }
        }

        private void UpdateEmptyVisibility()
        {
            Empty.Visibility = _viewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            TunnelList.Visibility = _viewModel.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            _viewModel.AddCommand.Execute(null);
        }

        private void OnStopAllClick(object sender, RoutedEventArgs e)
        {
            _viewModel.StopAllCommand.Execute(null);
        }

        private async void OnRowToggle(object sender, EventArgs e)
        {
            var row = sender as TunnelRow;
            if (row?.ViewModel == null)
            {
                return;
            }
            try
            {
                await _viewModel.ToggleTunnelAsync(row.ViewModel, LoopbackNoticeDialog.CheckAndPromptAsync);
            }
            catch (Exception ex)
            {
                AppLog.Error("TunnelsPivot", "Toggle failed", ex);
            }
        }

        private void OnRowEdit(object sender, EventArgs e)
        {
            var row = sender as TunnelRow;
            if (row?.ViewModel == null)
            {
                return;
            }
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.Navigate<TunnelEditPage>(TunnelEditArgs.Edit(row.ViewModel.Id));
            }
        }

        private async void OnRowDelete(object sender, EventArgs e)
        {
            var row = sender as TunnelRow;
            if (row?.ViewModel == null)
            {
                return;
            }
            string title = _loader.GetString("Tunnels_DeleteConfirmTitle") ?? "删除隧道";
            string msgFmt = _loader.GetString("Tunnels_DeleteConfirmMessage") ?? "确定要删除隧道「{0}」吗？";
            string message = string.Format(msgFmt, row.ViewModel.Name);
            string deleteText = _loader.GetString("Tunnels_Delete") ?? "删除";
            string cancelText = _loader.GetString("Tunnels_Cancel") ?? "取消";

            var result = await ConfirmDialog.ShowAsync(title, message, deleteText, cancelText, isDanger: true);
            if (result.Confirmed)
            {
                await _viewModel.DeleteTunnelAsync(row.ViewModel.Id);
            }
        }
    }
}
