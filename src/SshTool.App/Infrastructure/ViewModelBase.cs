using SshTool.Core.Common;
using SshTool.Core.Mvvm;

namespace SshTool.App.Infrastructure
{
    // ViewModel 基类：统一拿到导航、对话框与日志。
    public abstract class ViewModelBase : ObservableObject
    {
        protected NavigationService Navigation
        {
            get { return ServiceRegistry.Get<NavigationService>(); }
        }

        protected DialogService Dialogs
        {
            get { return ServiceRegistry.Get<DialogService>(); }
        }

        protected ILogger Logger
        {
            get { return ServiceRegistry.Get<ILogger>(); }
        }
    }
}
