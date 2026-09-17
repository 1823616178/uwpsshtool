using System.Collections.Generic;

namespace SshTool.Core.Common
{
    public enum RotationStepType
    {
        Delete,
        Rename
    }

    public sealed class RotationStep
    {
        public RotationStep(RotationStepType type, string sourcePath, string targetPath)
        {
            Type = type;
            SourcePath = sourcePath;
            TargetPath = targetPath;
        }

        public RotationStepType Type { get; }
        public string SourcePath { get; }

        // Delete 时为 null
        public string TargetPath { get; }
    }

    // 日志滚动（01-DESIGN.md §12.2 配套）：app.log 超 1 MiB 时滚动，最多保留 3 个历史文件。
    // 纯逻辑：只返回步骤，不碰文件系统，FileLogger 负责执行。
    public static class LogRotationPlanner
    {
        public const long DefaultMaxBytes = 1024 * 1024;
        public const int DefaultKeepCount = 3;

        // logPath：当前日志全路径；existingPaths：logs 目录里已存在的文件全路径集合。
        // 返回按执行顺序排列的步骤；未超限返回空列表。
        public static IReadOnlyList<RotationStep> Plan(
            string logPath,
            long currentSizeBytes,
            ISet<string> existingPaths,
            long maxBytes = DefaultMaxBytes,
            int keepCount = DefaultKeepCount)
        {
            var steps = new List<RotationStep>();
            if (currentSizeBytes < maxBytes || keepCount < 1)
            {
                return steps;
            }

            string oldest = logPath + "." + keepCount;
            if (existingPaths.Contains(oldest))
            {
                steps.Add(new RotationStep(RotationStepType.Delete, oldest, null));
            }
            for (int i = keepCount - 1; i >= 1; i--)
            {
                string source = logPath + "." + i;
                if (existingPaths.Contains(source))
                {
                    steps.Add(new RotationStep(RotationStepType.Rename, source, logPath + "." + (i + 1)));
                }
            }
            steps.Add(new RotationStep(RotationStepType.Rename, logPath, logPath + ".1"));
            return steps;
        }
    }
}
