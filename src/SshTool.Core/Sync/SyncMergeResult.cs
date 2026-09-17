using System.Collections.Generic;
using SshTool.Core.Sync.Protocol;

namespace SshTool.Core.Sync
{
    public sealed class SyncMergeResult
    {
        public SyncMergeResult(SyncDocumentV1 document, IReadOnlyList<SyncMergeConflict> conflicts)
        {
            Document = document;
            Conflicts = conflicts;
        }

        public SyncDocumentV1 Document { get; private set; }
        public IReadOnlyList<SyncMergeConflict> Conflicts { get; private set; }
    }
}
