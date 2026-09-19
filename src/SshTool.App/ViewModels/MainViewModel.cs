using System.Windows.Input;
using SshTool.App.Infrastructure;
using SshTool.App.Views;
using SshTool.Core.Mvvm;

namespace SshTool.App.ViewModels
{
    public sealed class MainViewModel : ViewModelBase
    {
        public MainViewModel()
        {
            AppServices services = AppServices.Current;
            if (services != null)
            {
                Hosts = new HostListViewModel(services);
                NewHostCommand = Hosts.NewHostCommand;
                SearchCommand = Hosts.ToggleSearchCommand;
                SignInCommand = Hosts.SignInCommand;
                SessionsPaneViewModel sessionsVm;
                if (!ServiceRegistry.TryGet(out sessionsVm))
                {
                    sessionsVm = new SessionsPaneViewModel(services);
                    ServiceRegistry.Register(sessionsVm);
                }
                Sessions = sessionsVm;
            }
            else
            {
                NewHostCommand = new RelayCommand(() => OpenPlaceholder("新建主机", "M3"));
                SearchCommand = new RelayCommand(() => OpenPlaceholder("搜索", "M3"));
                SignInCommand = new RelayCommand(() => OpenPlaceholder("登录", "M5"));
            }
            SyncCommand = new RelayCommand(() => OpenPlaceholder("同步", "M5"));
            SettingsCommand = new RelayCommand(() => Navigation.Navigate<SettingsPage>());
            KeysCommand = new RelayCommand(() => Navigation.Navigate<Views.Keys.KeysPage>());
            KnownHostsCommand = new RelayCommand(() => Navigation.Navigate<KnownHostsPage>());
            SnippetsCommand = new RelayCommand(() => Navigation.Navigate<SnippetsPage>(SnippetsArgs.Manage()));
            AppearanceCommand = new RelayCommand(() => Navigation.Navigate<AppearanceListPage>());
            AboutCommand = new RelayCommand(() => OpenPlaceholder("关于", "M8"));
        }

        public HostListViewModel Hosts { get; private set; }
        public SessionsPaneViewModel Sessions { get; private set; }

        public ICommand NewHostCommand { get; private set; }
        public ICommand SearchCommand { get; private set; }
        public ICommand SyncCommand { get; private set; }
        public ICommand SettingsCommand { get; private set; }
        public ICommand KeysCommand { get; private set; }
        public ICommand KnownHostsCommand { get; private set; }
        public ICommand SnippetsCommand { get; private set; }
        public ICommand AppearanceCommand { get; private set; }
        public ICommand AboutCommand { get; private set; }
        public ICommand SignInCommand { get; private set; }

        private void OpenPlaceholder(string title, string milestone)
        {
            Navigation.Navigate<PlaceholderPage>(new PlaceholderArgs(title, milestone));
        }
    }
}
