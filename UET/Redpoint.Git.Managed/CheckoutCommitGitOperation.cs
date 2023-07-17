namespace Redpoint.Git.Managed
{
    using Redpoint.Numerics;

    internal class CheckoutCommitGitOperation : GitOperation
    {
        public UInt160? PreviousCommit { get; set; }

        public UInt160 Commit { get; set; }

        public required DirectoryInfo GitDirectory { get; set; }

        public required DirectoryInfo TargetDirectory { get; set; }
    }

    internal class GetObjectFromPackfileGitOperation : GitOperation
    {
        public required FileInfo Packfile { get; set; }
    }
}