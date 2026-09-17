using SshTool.Core.Storage;
using Windows.Storage;

namespace SshTool.App.Platform
{
    // D04：ISettingsStore 的 UWP 实现，基于 ApplicationData.LocalSettings。
    public sealed class LocalSettingsStore : ISettingsStore
    {
        public bool TryGet(string key, out object value)
        {
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out value);
        }

        public void Set(string key, object value)
        {
            ApplicationData.Current.LocalSettings.Values[key] = value;
        }
    }
}
