namespace SshTool.App.Infrastructure
{
    // 02-UI-DESIGN.md §4：返回键按优先级依次询问注册的处理器；返回 true 表示已消费。
    // 弹层/页面各自注册（如终端选择模式、会话侧栏、未保存修改确认）。
    public interface IBackHandler
    {
        bool HandleBack();
    }
}
