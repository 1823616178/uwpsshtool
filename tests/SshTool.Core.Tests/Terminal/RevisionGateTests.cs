using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    // opt/full-pass：Pull 与 Feed 并发时的 Revision 竞态（TerminalView.OnTick）。
    public class RevisionGateTests
    {
        // 模拟原生屏幕：CopyDirtyRows 拷完之后、UI 线程再读 Revision 之前，SSH 线程又 Feed 了一批。
        private sealed class RacyScreen
        {
            public long Revision;
            public int Content;
            public bool Dirty;
            public bool FeedDuringNextCopy;

            public void Feed()
            {
                Content++;
                Revision++;
                Dirty = true;
            }

            public bool CopyDirtyRows(out int copied)
            {
                copied = Content;
                if (!Dirty)
                {
                    return false;
                }
                Dirty = false;
                if (FeedDuringNextCopy)
                {
                    FeedDuringNextCopy = false;
                    Feed(); // 并发落地：脏位重新置起、Revision 前进
                }
                return true;
            }
        }

        [Fact]
        public void OldPattern_ReadAfterPull_LosesConcurrentFeed()
        {
            // 记录旧写法的缺陷，防止有人「简化」回去
            var screen = new RacyScreen();
            long last = -1;
            int shown = 0;
            screen.Feed();
            screen.FeedDuringNextCopy = true;
            for (int frame = 0; frame < 5; frame++)
            {
                if (screen.Revision != last)
                {
                    int copied;
                    if (screen.CopyDirtyRows(out copied))
                    {
                        shown = copied;
                    }
                    last = screen.Revision; // Pull 之后再读：并发 Feed 的修订号被当成已消费
                }
            }
            Assert.Equal(1, shown);
            Assert.Equal(2, screen.Content); // 第二批内容一直没画出来
        }

        [Fact]
        public void Gate_SnapshotBeforePull_PicksUpConcurrentFeedNextFrame()
        {
            var screen = new RacyScreen();
            var gate = new RevisionGate();
            int shown = 0;
            int pulls = 0;
            screen.Feed();
            screen.FeedDuringNextCopy = true;
            for (int frame = 0; frame < 5; frame++)
            {
                long rev = screen.Revision;
                if (gate.NeedsPull(rev))
                {
                    pulls++;
                    int copied;
                    if (screen.CopyDirtyRows(out copied))
                    {
                        shown = copied;
                    }
                    gate.Commit(rev);
                }
            }
            Assert.Equal(2, shown);
            Assert.Equal(2, pulls); // 只多拉一次，之后稳定
        }

        [Fact]
        public void Gate_RepullWithoutDirtyRows_IsHarmless()
        {
            // Feed 落在「读快照」与「CopyDirtyRows」之间：内容已拷到，下一帧的重拉没有脏行
            var screen = new RacyScreen();
            var gate = new RevisionGate();
            screen.Feed();
            long rev = screen.Revision;
            Assert.True(gate.NeedsPull(rev));
            screen.Feed(); // 并发
            int copied;
            Assert.True(screen.CopyDirtyRows(out copied));
            Assert.Equal(2, copied);
            gate.Commit(rev);

            long rev2 = screen.Revision;
            Assert.True(gate.NeedsPull(rev2));
            Assert.False(screen.CopyDirtyRows(out copied)); // 无脏行：TerminalView 不再整窗兜底
            gate.Commit(rev2);
            Assert.False(gate.NeedsPull(screen.Revision));
        }

        [Fact]
        public void Reset_ForcesNextPull()
        {
            var gate = new RevisionGate();
            gate.Commit(7);
            Assert.False(gate.NeedsPull(7));
            gate.Reset();
            Assert.True(gate.NeedsPull(7));
            Assert.True(gate.NeedsPull(0));
        }
    }
}
