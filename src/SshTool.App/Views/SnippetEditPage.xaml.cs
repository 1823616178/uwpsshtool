using System.Collections.Generic;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels.Snippets;
using SshTool.Core.Common;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed class SnippetEditArgs
    {
        public string SnippetId { get; set; }

        public static SnippetEditArgs New()
        {
            return new SnippetEditArgs();
        }

        public static SnippetEditArgs Edit(string snippetId)
        {
            return new SnippetEditArgs { SnippetId = snippetId };
        }
    }

    public sealed partial class SnippetEditPage : Page, IBackHandler
    {
        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();
        // R01 (C-02)：导航世代，离开后加载链不再触碰 UI。
        private readonly NavigationLifetime _lifetime = new NavigationLifetime();
        private bool _abandonConfirmed;
        private bool _suppress;

        public SnippetEditPage()
        {
            ViewModel = new SnippetEditViewModel(AppServices.Current);
            this.InitializeComponent();
        }

        public SnippetEditViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            int generation = _lifetime.Begin();
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RegisterBackHandler(this);
            }
            var args = e.Parameter as SnippetEditArgs;
            LoadAsync(generation, args == null ? null : args.SnippetId).Forget("SnippetEditPage.Load", AppLog.Logger);
        }

        // R01 (C-02)：await 后先查世代，页面已离开则不再 BindLoaded 触碰 XAML。
        private async System.Threading.Tasks.Task LoadAsync(int generation, string snippetId)
        {
            try
            {
                await ViewModel.LoadAsync(snippetId);
                if (!_lifetime.IsCurrent(generation))
                {
                    return;
                }
                BindLoaded();
            }
            catch (System.Exception ex)
            {
                AppLog.Error("SnippetEdit", "片段编辑页加载失败", ex);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _lifetime.End();
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.UnregisterBackHandler(this);
            }
            base.OnNavigatedFrom(e);
        }

        public bool HandleBack()
        {
            if (_abandonConfirmed || !ViewModel.IsDirty)
            {
                return false;
            }
            ConfirmAbandonAsync().Forget("SnippetEditPage.ConfirmAbandon", AppLog.Logger);
            return true;
        }

        private void BindLoaded()
        {
            TitleText.Text = ViewModel.Title;
            _suppress = true;
            NameBox.Text = ViewModel.Name;
            GroupBox.Text = ViewModel.GroupName;
            ContentBox.Text = ViewModel.Content;
            SendEnterBox.IsChecked = ViewModel.SendEnter;
            VariableHint.Text = ViewModel.VariableHint;
            _suppress = false;
            ShowErrors();
        }

        private async System.Threading.Tasks.Task ConfirmAbandonAsync()
        {
            ConfirmDialogResult result = await ConfirmDialog.ShowAsync(
                "放弃修改？", "未保存的更改将丢失。", "放弃", "继续编辑");
            if (result.Confirmed)
            {
                _abandonConfirmed = true;
                NavigationService nav;
                if (ServiceRegistry.TryGet(out nav))
                {
                    nav.GoBack();
                }
            }
        }

        private async void OnSaveClick(object sender, RoutedEventArgs e)
        {
            bool ok = await ViewModel.SaveAsync();
            if (!ok)
            {
                SyncFromViewModel();
                ShowErrors();
                FocusField(ViewModel.FirstErrorField());
            }
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.IsNew)
            {
                NavigationService nav;
                if (ServiceRegistry.TryGet(out nav))
                {
                    nav.GoBack();
                }
                return;
            }
            await ViewModel.DeleteAsync();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            if (!HandleBack())
            {
                NavigationService nav;
                if (ServiceRegistry.TryGet(out nav))
                {
                    nav.GoBack();
                }
            }
        }

        private void OnNameChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress)
            {
                return;
            }
            ViewModel.Name = NameBox.Text;
            ShowErrors();
        }

        private void OnGroupTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.SuggestionChosen)
            {
                return;
            }
            if (!_suppress)
            {
                ViewModel.GroupName = sender.Text;
            }
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                sender.ItemsSource = FilterGroups(sender.Text);
            }
        }

        private void OnGroupSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            string picked = args.SelectedItem as string;
            if (!string.IsNullOrEmpty(picked))
            {
                sender.Text = picked;
                ViewModel.GroupName = picked;
            }
        }

        private void OnContentChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress)
            {
                return;
            }
            ViewModel.Content = ContentBox.Text;
            VariableHint.Text = ViewModel.VariableHint;
            ShowErrors();
        }

        private void OnSendEnterChanged(object sender, RoutedEventArgs e)
        {
            if (_suppress)
            {
                return;
            }
            ViewModel.SendEnter = SendEnterBox.IsChecked == true;
        }

        private void SyncFromViewModel()
        {
            _suppress = true;
            NameBox.Text = ViewModel.Name;
            GroupBox.Text = ViewModel.GroupName;
            ContentBox.Text = ViewModel.Content;
            SendEnterBox.IsChecked = ViewModel.SendEnter;
            VariableHint.Text = ViewModel.VariableHint;
            _suppress = false;
        }

        private void ShowErrors()
        {
            SetError(NameError, "name");
            SetError(ContentError, "content");
        }

        private void SetError(TextBlock block, string field)
        {
            string key;
            if (ViewModel.Errors != null && ViewModel.Errors.TryGetValue(field, out key))
            {
                string text = _loader.GetString(key);
                block.Text = string.IsNullOrEmpty(text) ? key : text;
                block.Visibility = Visibility.Visible;
            }
            else
            {
                block.Text = string.Empty;
                block.Visibility = Visibility.Collapsed;
            }
        }

        private void FocusField(string field)
        {
            if (string.IsNullOrEmpty(field))
            {
                return;
            }
            Control target = null;
            if (field == "name")
            {
                target = NameBox;
            }
            else if (field == "content")
            {
                target = ContentBox;
            }
            if (target != null)
            {
                Control focus = target;
                DispatcherHelper.Post(() => focus.Focus(FocusState.Programmatic));
            }
        }

        private IList<string> FilterGroups(string text)
        {
            var result = new List<string>();
            IList<string> all = ViewModel.ExistingGroups;
            if (all == null)
            {
                return result;
            }
            string q = text == null ? string.Empty : text.Trim();
            for (int i = 0; i < all.Count; i++)
            {
                if (q.Length == 0
                    || all[i].IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.Add(all[i]);
                }
            }
            return result;
        }
    }
}
