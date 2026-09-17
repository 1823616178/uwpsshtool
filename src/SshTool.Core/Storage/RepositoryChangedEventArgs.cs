using System;
using System.Collections.Generic;

namespace SshTool.Core.Storage
{
    public sealed class RepositoryChangedEventArgs : EventArgs
    {
        public RepositoryChangedEventArgs(ChangeOrigin origin, IReadOnlyList<string> changedIds)
        {
            Origin = origin;
            ChangedIds = changedIds ?? new string[0];
        }

        public ChangeOrigin Origin { get; private set; }
        public IReadOnlyList<string> ChangedIds { get; private set; }
    }
}
