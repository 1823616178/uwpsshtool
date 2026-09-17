using SshTool.Core.Mvvm;
using Xunit;

namespace SshTool.Core.Tests.Mvvm
{
    public class DialogQueueTests
    {
        [Fact]
        public void Enqueue_FirstBecomesCurrent()
        {
            var q = new DialogQueue();
            q.Enqueue("A");
            Assert.Equal("A", q.Current);
            Assert.Equal(0, q.PendingCount);
        }

        [Fact]
        public void CompleteCurrent_PromotesInFifoOrder()
        {
            var q = new DialogQueue();
            q.Enqueue("A");
            q.Enqueue("B");
            q.Enqueue("C");
            Assert.Equal(2, q.PendingCount);
            Assert.Equal("B", q.CompleteCurrent());
            Assert.Equal("C", q.CompleteCurrent());
            Assert.Null(q.CompleteCurrent());
            Assert.Null(q.Current);
        }

        [Fact]
        public void Cancel_Pending_RemovesOnlyIt()
        {
            var q = new DialogQueue();
            q.Enqueue("A");
            q.Enqueue("B");
            q.Enqueue("C");
            Assert.True(q.Cancel("B"));
            Assert.Equal(1, q.PendingCount);
            Assert.Equal("A", q.Current);
            Assert.Equal("C", q.CompleteCurrent());
        }

        [Fact]
        public void Cancel_Current_PromotesNext()
        {
            var q = new DialogQueue();
            q.Enqueue("A");
            q.Enqueue("B");
            Assert.True(q.Cancel("A"));
            Assert.Equal("B", q.Current);
        }

        [Fact]
        public void Cancel_Unknown_ReturnsFalse()
        {
            var q = new DialogQueue();
            q.Enqueue("A");
            Assert.False(q.Cancel("X"));
            Assert.Equal("A", q.Current);
        }
    }
}
