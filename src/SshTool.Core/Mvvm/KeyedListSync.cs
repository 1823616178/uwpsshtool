using System;
using System.Collections.Generic;

namespace SshTool.Core.Mvvm
{
    public struct KeyedListSyncStats
    {
        public int Inserted;
        public int Removed;
        public int Moved;
        public int Replaced;
        public int Kept;
        public bool WasReset;
    }

    // ui/fix-pass：把 target（通常是 ObservableCollection）就地改成与 source 同序同内容，只发最少的
    // Insert/Remove/Move/Replace 通知。旧实现每次刷新 Clear() + 逐个 Add，ListView 全量重建容器、
    // 进场动画整屏重放——低端 ARM（Lumia 950）上状态点一变整个列表闪一次。
    //   keyOf：身份键（同键视为同一条）；sameContent：同键且内容相同 → 保留旧实例（onKept 回调里可继续
    //   同步子集合）；内容不同 → Replace 为新实例。move 为空时用 RemoveAt + Insert 代替。
    //   source 内有重复键（异常数据）时退化为整表重置，保证结果正确。
    public static class KeyedListSync
    {
        public static KeyedListSyncStats Sync<T>(
            IList<T> target,
            IReadOnlyList<T> source,
            Func<T, string> keyOf,
            Func<T, T, bool> sameContent,
            Action<int, int> move = null,
            Action<T, T> onKept = null)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }
            if (keyOf == null)
            {
                throw new ArgumentNullException(nameof(keyOf));
            }
            if (sameContent == null)
            {
                throw new ArgumentNullException(nameof(sameContent));
            }
            var stats = new KeyedListSyncStats();
            int count = source == null ? 0 : source.Count;

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                if (!wanted.Add(keyOf(source[i]) ?? string.Empty))
                {
                    return Reset(target, source);
                }
            }

            // 1) 删掉 source 里没有的（以及 target 自身重复的）键，从后往前删不扰动前面的下标。
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var firstIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < target.Count; i++)
            {
                string k = keyOf(target[i]) ?? string.Empty;
                if (!firstIndex.ContainsKey(k))
                {
                    firstIndex[k] = i;
                }
            }
            for (int i = target.Count - 1; i >= 0; i--)
            {
                string k = keyOf(target[i]) ?? string.Empty;
                if (!wanted.Contains(k) || firstIndex[k] != i)
                {
                    target.RemoveAt(i);
                    stats.Removed++;
                }
                else
                {
                    seen.Add(k);
                }
            }

            // 2) 逐位对齐：同键原位 → 判等；别处有 → 挪过来；没有 → 插入。
            for (int i = 0; i < count; i++)
            {
                T incoming = source[i];
                string k = keyOf(incoming) ?? string.Empty;
                if (i < target.Count && string.Equals(keyOf(target[i]) ?? string.Empty, k, StringComparison.Ordinal))
                {
                    Reconcile(target, i, incoming, sameContent, onKept, ref stats);
                    continue;
                }
                if (seen.Contains(k))
                {
                    int from = IndexOfKey(target, keyOf, k, i + 1);
                    if (move != null)
                    {
                        move(from, i);
                    }
                    else
                    {
                        T item = target[from];
                        target.RemoveAt(from);
                        target.Insert(i, item);
                    }
                    stats.Moved++;
                    Reconcile(target, i, incoming, sameContent, onKept, ref stats);
                    continue;
                }
                target.Insert(i, incoming);
                stats.Inserted++;
            }

            // 3) 理论上不会剩；防御性截尾。
            while (target.Count > count)
            {
                target.RemoveAt(target.Count - 1);
                stats.Removed++;
            }
            return stats;
        }

        private static void Reconcile<T>(IList<T> target, int index, T incoming, Func<T, T, bool> sameContent,
            Action<T, T> onKept, ref KeyedListSyncStats stats)
        {
            T existing = target[index];
            if (sameContent(existing, incoming))
            {
                stats.Kept++;
                if (onKept != null)
                {
                    onKept(existing, incoming);
                }
            }
            else
            {
                target[index] = incoming;
                stats.Replaced++;
            }
        }

        private static int IndexOfKey<T>(IList<T> target, Func<T, string> keyOf, string key, int start)
        {
            for (int j = start; j < target.Count; j++)
            {
                if (string.Equals(keyOf(target[j]) ?? string.Empty, key, StringComparison.Ordinal))
                {
                    return j;
                }
            }
            throw new InvalidOperationException("KeyedListSync: key vanished during sync");
        }

        private static KeyedListSyncStats Reset<T>(IList<T> target, IReadOnlyList<T> source)
        {
            target.Clear();
            for (int i = 0; i < source.Count; i++)
            {
                target.Add(source[i]);
            }
            return new KeyedListSyncStats { WasReset = true, Inserted = source.Count };
        }
    }
}
