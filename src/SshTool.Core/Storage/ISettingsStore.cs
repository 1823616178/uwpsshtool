namespace SshTool.Core.Storage
{
    // 设置持久层抽象（01-DESIGN §8.3）。值只放 string/int/bool；
    // App 侧由 ApplicationData.LocalSettings 实现（LocalSettingsStore）。
    public interface ISettingsStore
    {
        bool TryGet(string key, out object value);
        void Set(string key, object value);
    }
}
