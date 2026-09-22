using System;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.App.ViewModels.Sync;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Vault;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Sync
{
    // U19 同步冲突页（02-UI-DESIGN.md §5.14）。
    // 读取当前冲突摘要，按 reason 显示标题/说明/按钮；字段列表经 ConflictPresenter 脱敏
    // （敏感字段显示「有变更」）。按钮调用 SyncCoordinator.ResolveConflictAsync。
    public sealed partial class SyncConflictPage : Page
    {
        public SyncConflictPage()
        {
            ViewModel = CreateViewModel();
            this.InitializeComponent();
            ViewModel.PropertyChanged += OnViewModelChanged;
        }

        public SyncConflictViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Header.Title = Localized.Get("SyncConflict_Title", "同步冲突");
            Header.ShowBackButton = Frame.CanGoBack;
            BindAll();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            base.OnNavigatedFrom(e);
        }

        private static SyncConflictViewModel CreateViewModel()
        {
            AppServices services = AppServices.Current;
            SyncCoordinator sync = services != null ? services.Sync : null;
            return new SyncConflictViewModel(sync, new AppEntityLookup(services));
        }

        private void BindAll()
        {
            if (!ViewModel.HasConflict)
            {
                NoConflictEmpty.Glyph = "\uE7BA";
                NoConflictEmpty.Title = Localized.Get("SyncConflict_Empty", "暂无待处理的冲突");
                NoConflictEmpty.Description = string.Empty;
                NoConflictEmpty.Visibility = Visibility.Visible;
                SummaryCard.Visibility = Visibility.Collapsed;
                FieldsHeader.Visibility = Visibility.Collapsed;
                FieldsList.Visibility = Visibility.Collapsed;
                PrimaryButton.Visibility = Visibility.Collapsed;
                SecondaryButton.Visibility = Visibility.Collapsed;
                return;
            }
            NoConflictEmpty.Visibility = Visibility.Collapsed;
            SummaryCard.Visibility = Visibility.Visible;
            FieldsHeader.Visibility = Visibility.Visible;
            FieldsList.Visibility = Visibility.Visible;
            PrimaryButton.Visibility = Visibility.Visible;
            SecondaryButton.Visibility = Visibility.Visible;

            TitleText.Text = ViewModel.Title;
            DescriptionText.Text = ViewModel.Description;
            RemoteSummaryText.Text = ViewModel.RemoteSummaryText;
            PrimaryButton.Content = ViewModel.PrimaryButtonText;
            SecondaryButton.Content = ViewModel.SecondaryButtonText;

            string remote = ViewModel.RemoteUpdatedAt;
            string local = ViewModel.LocalUpdatedAt;
            if (!string.IsNullOrEmpty(remote) && !string.IsNullOrEmpty(local))
            {
                UpdatedAtText.Text = Localized.Format("SyncConflict_UpdatedBoth", "云端更新于 {0} · 本机更新于 {1}", remote, local);
            }
            else if (!string.IsNullOrEmpty(remote))
            {
                UpdatedAtText.Text = Localized.Format("SyncConflict_UpdatedRemote", "云端更新于 {0}", remote);
            }
            else
            {
                UpdatedAtText.Visibility = Visibility.Collapsed;
            }

            FieldsList.ItemsSource = ViewModel.Fields;
            if (!ViewModel.HasFields)
            {
                FieldsHeader.Visibility = Visibility.Collapsed;
                FieldsList.Visibility = Visibility.Collapsed;
            }
        }

        private void RefreshState()
        {
            PrimaryButton.IsEnabled = !ViewModel.IsResolving;
            SecondaryButton.IsEnabled = !ViewModel.IsResolving;
            Progress.Visibility = ViewModel.IsResolving ? Visibility.Visible : Visibility.Collapsed;
            if (ViewModel.HasError)
            {
                ErrorText.Text = ViewModel.ErrorMessage;
                ErrorText.Visibility = Visibility.Visible;
            }
            else
            {
                ErrorText.Visibility = Visibility.Collapsed;
            }
        }

        private void OnViewModelChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            string name = e.PropertyName;
            if (name == "Title" || name == "Description" || name == "RemoteSummaryText"
                || name == "PrimaryButtonText" || name == "SecondaryButtonText"
                || name == "HasConflict" || name == "HasFields")
            {
                BindAll();
            }
            RefreshState();
        }

        private void OnPrimaryClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.UseRemoteCommand.CanExecute(null))
            {
                ViewModel.UseRemoteCommand.Execute(null);
            }
        }

        private void OnSecondaryClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.KeepLocalCommand.CanExecute(null))
            {
                ViewModel.KeepLocalCommand.Execute(null);
            }
        }

        private void OnBackRequested(object sender, EventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
