using System;
using Windows.UI.ViewManagement;

namespace SshTool.App.Infrastructure
{
    /// <summary>
    /// Continuum 与鼠标模式辅助。依据 02-UI-DESIGN.md §3 检测当前视图是否处于鼠标交互模式。
    /// </summary>
    public static class InteractionModeHelper
    {
        public static bool IsMouseMode
        {
            get
            {
                try
                {
                    var viewSettings = UIViewSettings.GetForCurrentView();
                    return viewSettings != null && viewSettings.UserInteractionMode == UserInteractionMode.Mouse;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
