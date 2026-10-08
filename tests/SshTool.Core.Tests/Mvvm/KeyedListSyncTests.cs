using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using SshTool.Core.Hosts;
using SshTool.Core.Mvvm;
using Xunit;

namespace SshTool.Core.Tests.Mvvm
{
    public class KeyedListSyncTests
    {
        private sealed class Item
        {
            public Item(string key, int value)
            {
                Key = key;
                Value = value;
            }

            public string Key { get; }
            public int Value { get; }
        }

        private static List<Item> Items(params string[] spec)
        {
            // "a" → (a,0)；"a=3" → (a,3)
            return spec.Select(s =>
            {
                string[] p = s.Split('=');
                return new Item(p[0], p.Length > 1 ? int.Parse(p[1]) : 0);
            }).ToList();
        }

        private static KeyedListSyncStats Run(ObservableCollection<Item> target, List<Item> source,
            List<NotifyCollectionChangedAction> log)
        {
            NotifyCollectionChangedEventHandler h = (s, e) => log.Add(e.Action);
            target.CollectionChanged += h;
            try
            {
                return KeyedListSync.Sync(target, source, i => i.Key, (a, b) => a.Value == b.Value, target.Move);
            }
            finally
            {
                target.CollectionChanged -= h;
            }
        }

        private static void AssertSameSequence(IList<Item> actual, List<Item> expected)
        {
            Assert.Equal(expected.Select(i => i.Key + "=" + i.Value), actual.Select(i => i.Key + "=" + i.Value));
        }

        [Fact]
        public void IdenticalContent_NoNotifications_KeepsInstances()
        {
            var target = new ObservableCollection<Item>(Items("a", "b", "c"));
            Item originalB = target[1];
            var log = new List<NotifyCollectionChangedAction>();
            KeyedListSyncStats stats = Run(target, Items("a", "b", "c"), log);
            Assert.Empty(log);
            Assert.Equal(3, stats.Kept);
            Assert.Same(originalB, target[1]);
        }

        [Fact]
        public void SingleChangedRow_IsOneReplace()
        {
            var target = new ObservableCollection<Item>(Items("a", "b", "c"));
            var log = new List<NotifyCollectionChangedAction>();
            Run(target, Items("a", "b=1", "c"), log);
            Assert.Equal(new[] { NotifyCollectionChangedAction.Replace }, log);
            Assert.Equal(1, target[1].Value);
        }

        [Fact]
        public void InsertAndRemove_AreSingleOps()
        {
            var target = new ObservableCollection<Item>(Items("a", "b", "c"));
            var log = new List<NotifyCollectionChangedAction>();
            List<Item> source = Items("a", "x", "c");
            Run(target, source, log);
            AssertSameSequence(target, source);
            Assert.Equal(new[] { NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add }, log);
        }

        [Fact]
        public void Reorder_UsesMove()
        {
            var target = new ObservableCollection<Item>(Items("a", "b", "c"));
            var log = new List<NotifyCollectionChangedAction>();
            List<Item> source = Items("c", "a", "b");
            KeyedListSyncStats stats = Run(target, source, log);
            AssertSameSequence(target, source);
            Assert.All(log, a => Assert.Equal(NotifyCollectionChangedAction.Move, a));
            Assert.Equal(1, stats.Moved);
        }

        [Fact]
        public void DuplicateSourceKeys_FallBackToReset()
        {
            var target = new ObservableCollection<Item>(Items("a"));
            var log = new List<NotifyCollectionChangedAction>();
            List<Item> source = Items("b", "b");
            KeyedListSyncStats stats = Run(target, source, log);
            Assert.True(stats.WasReset);
            AssertSameSequence(target, source);
        }

        [Fact]
        public void EmptyAndNullSource_ClearTarget()
        {
            var target = new ObservableCollection<Item>(Items("a", "b"));
            KeyedListSync.Sync(target, null, i => i.Key, (a, b) => true);
            Assert.Empty(target);
        }

        [Fact]
        public void WithoutMoveCallback_StillProducesSourceOrder()
        {
            var target = new List<Item>(Items("a", "b", "c", "d"));
            List<Item> source = Items("d", "c", "b", "a");
            KeyedListSync.Sync(target, source, i => i.Key, (a, b) => a.Value == b.Value);
            AssertSameSequence(target, source);
        }

        [Fact]
        public void OnKept_ReceivesOldAndNew()
        {
            var target = new List<Item>(Items("a"));
            Item old = target[0];
            Item incoming = null;
            KeyedListSync.Sync(target, Items("a"), i => i.Key, (a, b) => true, null, (o, n) =>
            {
                Assert.Same(old, o);
                incoming = n;
            });
            Assert.NotNull(incoming);
            Assert.Same(old, target[0]);
        }

        [Fact]
        public void RandomizedSequences_AlwaysConverge()
        {
            var rng = new Random(1234);
            string[] pool = Enumerable.Range(0, 12).Select(i => "k" + i).ToArray();
            for (int round = 0; round < 500; round++)
            {
                var target = new ObservableCollection<Item>(Pick(rng, pool));
                List<Item> source = Pick(rng, pool);
                var log = new List<NotifyCollectionChangedAction>();
                Run(target, source, log);
                AssertSameSequence(target, source);
                Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, log);
            }
        }

        private static List<Item> Pick(Random rng, string[] pool)
        {
            return pool.OrderBy(_ => rng.Next()).Take(rng.Next(pool.Length + 1))
                .Select(k => new Item(k, rng.Next(3))).ToList();
        }

        [Fact]
        public void HostListRow_ContentEquals_ComparesDisplayedFields()
        {
            var a = new HostListRow { HostId = "1", Name = "n", Status = HostListStatus.None };
            var b = new HostListRow { HostId = "1", Name = "n", Status = HostListStatus.None };
            Assert.True(HostListRow.ContentEquals(a, b));
            b.IsFavorite = true;
            Assert.False(HostListRow.ContentEquals(a, b));
        }

        [Fact]
        public void HostListGroup_HeaderEquals_IgnoresRows()
        {
            var a = new HostListGroup { GroupId = "g", Name = "G", HostCount = 2, Rows = new HostListRow[0] };
            var b = new HostListGroup { GroupId = "g", Name = "G", HostCount = 2, Rows = new[] { new HostListRow() } };
            Assert.True(HostListGroup.HeaderEquals(a, b));
            b.HostCount = 3;
            Assert.False(HostListGroup.HeaderEquals(a, b));
        }
    }
}
