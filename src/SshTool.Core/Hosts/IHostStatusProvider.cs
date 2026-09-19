namespace SshTool.Core.Hosts
{
    public interface IHostStatusProvider
    {
        HostListStatus GetStatus(string hostId);
    }

    public sealed class NullHostStatusProvider : IHostStatusProvider
    {
        public static readonly NullHostStatusProvider Instance = new NullHostStatusProvider();

        private NullHostStatusProvider()
        {
        }

        public HostListStatus GetStatus(string hostId)
        {
            return HostListStatus.None;
        }
    }
}
