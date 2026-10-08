namespace SshTool.Core.Terminal
{
    // opt/full-pass：渲染帧的「内容是否变了」判定。
    //
    // 旧写法是 Pull 之后再读一次 Revision 记作已消费：
    //     if (screen.Revision != last) { Pull(); last = screen.Revision; }
    // Pull 与第二次读之间若有 Feed 落地（SSH 读线程与 UI 线程并发），这批脏行的
    // Revision 被当成「已画过」吞掉，直到下一批输出到来才会显示——典型症状是
    // 命令输出最后一行/提示符偶发不出现，按一下键才刷出来。
    //
    // 正确做法：Pull 之前取快照，Pull 之后只提交这份快照。期间到达的新内容让
    // Revision 继续领先，下一帧必然再拉一次。
    public sealed class RevisionGate
    {
        private const long Unset = long.MinValue;

        private long _consumed = Unset;

        public long Consumed
        {
            get { return _consumed; }
        }

        // revisionBeforePull 必须是 Pull 之前读到的值。
        public bool NeedsPull(long revisionBeforePull)
        {
            return revisionBeforePull != _consumed;
        }

        public void Commit(long revisionBeforePull)
        {
            _consumed = revisionBeforePull;
        }

        public void Reset()
        {
            _consumed = Unset;
        }
    }
}
