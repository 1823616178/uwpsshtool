using System;
using System.ComponentModel;
using System.Threading.Tasks;
using SshTool.App.Controls;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using SshTool.Core.Common;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views.Main
{
    public sealed partial class TunnelsPivot : UserControl
    {
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
            if (e.PropertyName == nameof(TunnelsViewModel.IsEmpty) || e.PropertyName == nameof(TunnelsViewModel.HasError))
            {
                UpdateEmptyVisibility();
            }
        }

        private void UpdateEmptyVisibility()
        {
            bool hasError = _viewModel.HasError;
            ErrorState.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
            Empty.Visibility = (!hasError && _viewModel.IsEmpty) ? Visibility.Visible : Visibility.Collapsed;
            TunnelList.Visibility = (!hasError && !_viewModel.IsEmpty) ? Visibility.Visible : Visibility.Collapsed;
            if (hasError)
            {
                ErrorState.Title = Localized.Get("Common_LoadFailed", "加载失败");
                ErrorState.Description = _viewModel.ErrorMessage;
                ErrorState.PrimaryText = Localized.Get("Common_Retry", "重试");
                ErrorState.PrimaryCommand = _viewModel.RefreshCommand;
            }
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
            try
            {
                var row = sender as TunnelRow;
                if (row?.ViewModel == null)
                {
                    return;
                }
                string title = Localized.Get("Tunnels_DeleteConfirmTitle", "删除隧道");
                string msgFmt = Localized.Get("Tunnels_DeleteConfirmMessage", "确定要删除隧道「{0}」吗？");
                string message = string.Format(msgFmt, row.ViewModel.Name);
                string deleteText = Localized.Get("Tunnels_Delete", "删除");
                string cancelText = Localized.Get("Tunnels_Cancel", "取消");

                var result = await ConfirmDialog.ShowAsync(title, message, deleteText, cancelText, isDanger: true);
                if (result.Confirmed)
                {
                    await _viewModel.DeleteTunnelAsync(row.ViewModel.Id);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("TunnelsPivot", "OnRowDelete failed", ex);
            }
        }
    }
}
