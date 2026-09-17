using System.Linq;
using SshTool.Core.Storage;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    // U11 TabSet：标签集合 + 活动标签 + 序列化
    public class TabSetTests
    {
        [Fact]
        public void Add_CreatesSingleLeafTabAndActivates()
        {
            var set = new TabSet();
            var tab = set.Add("t1", "web-01", "s1");

            Assert.Equal("t1", tab.TabId);
            Assert.Equal("web-01", tab.Title);
            Assert.True(tab.Tree.Contains("s1"));
            Assert.Equal("t1", set.ActiveTabId);
            Assert.Same(tab, set.ActiveTab);
        }

        [Fact]
        public void Add_DuplicateTabId_Throws()
        {
            var set = new TabSet();
            set.Add("t1", "a", "s1");
            Assert.Throws<System.ArgumentException>(() => set.Add("t1", "b", "s2"));
        }

        [Fact]
        public void Close_ActiveTab_ActivatesRightNeighbor()
        {
            var set = new TabSet();
            set.Add("t1", "a", "s1");
            set.Add("t2", "b", "s2");
            set.Add("t3", "c", "s3");
            set.Activate("t2");

            Assert.True(set.Close("t2"));

            Assert.Equal("t3", set.ActiveTabId); // 关闭后同下标 = 原右侧
            Assert.Equal(2, set.Tabs.Count);
        }

        [Fact]
        public void Close_ActiveRightmost_FallsBackLeft()
        {
            var set = new TabSet();
            set.Add("t1", "a", "s1");
            set.Add("t2", "b", "s2");

            set.Close("t2"); // t2 是 active（Add 成为活动）且最右

            Assert.Equal("t1", set.ActiveTabId);
        }

        [Fact]
        public void Close_NonActive_KeepsActive()
        {
            var set = new TabSet();
            set.Add("t1", "a", "s1");
            set.Add("t2", "b", "s2"); // t2 active

            set.Close("t1");

            Assert.Equal("t2", set.ActiveTabId);
        }

        [Fact]
        public void Close_LastTab_ActiveBecomesNull()
        {
            var set = new TabSet();
            set.Add("t1", "a", "s1");

            set.Close("t1");

            Assert.Empty(set.Tabs);
            Assert.Null(set.ActiveTabId);
            Assert.Null(set.ActiveTab);
        }

        [Fact]
        public void Close_Unknown_ReturnsFalse()
        {
            var set = new TabSet();
            Assert.False(set.Close("nope"));
        }

        [Fact]
        public void Activate_Unknown_ReturnsFalse()
        {
            var set = new TabSet();
            set.Add("t1", "a", "s1");
            Assert.False(set.Activate("nope"));
            Assert.Equal("t1", set.ActiveTabId);
        }

        [Fact]
        public void Changed_FiresOnMutations()
        {
            var set = new TabSet();
            int count = 0;
            set.Changed += delegate { count++; };

            set.Add("t1", "a", "s1");
            set.Add("t2", "b", "s2");
            set.Activate("t1");
            set.Activate("t1"); // 已是活动，不重复触发
            set.Close("t2");

            Assert.Equal(4, count);
        }

        [Fact]
        public void Serialization_RoundTrip()
        {
            var set = new TabSet();
            set.Add("t1", "web", "s1");
            set.Add("t2", "db", "s2");
            set.Tabs[1].Tree.Split("s2", SplitOrientation.Row, "s3", 0.4);
            set.Activate("t1");

            var restored = TabSet.FromJson(JsonText.ParseObject(set.ToJson().ToString(Newtonsoft.Json.Formatting.None)));

            Assert.Equal(new[] { "t1", "t2" }, restored.Tabs.Select(t => t.TabId));
            Assert.Equal("t1", restored.ActiveTabId);
            Assert.Equal("db", restored.Tabs[1].Title);
            Assert.Equal(new[] { "s2", "s3" }, restored.Tabs[1].Tree.Leaves().Select(l => l.SessionId));
            Assert.Equal(0.4, ((SplitNode)restored.Tabs[1].Tree.Root).Ratio, 6);
        }

        [Fact]
        public void Serialization_BadJson_Throws()
        {
            Assert.Throws<System.FormatException>(() => TabSet.FromJson(JsonText.ParseObject(@"{}")));
            Assert.Throws<System.FormatException>(() => TabSet.FromJson(JsonText.ParseObject(
                @"{""tabs"":[{""tabId"":""t1""}]}")));
        }

        [Fact]
        public void Serialization_UnknownActive_FallsBackToFirstTab()
        {
            var set = new TabSet();
            set.Add("t1", "a", "s1");
            var json = set.ToJson();
            json["activeTabId"] = "ghost";

            var restored = TabSet.FromJson(json);

            Assert.Equal("t1", restored.ActiveTabId);
        }
    }
}
