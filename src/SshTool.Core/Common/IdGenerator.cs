using System;

namespace SshTool.Core.Common
{
    public static class IdGenerator
    {
        // §8.1：UUID v4 小写
        public static string NewId()
        {
            return Guid.NewGuid().ToString("D");
        }
    }
}
