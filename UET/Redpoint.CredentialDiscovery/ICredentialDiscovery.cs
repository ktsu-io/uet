namespace Redpoint.CredentialDiscovery
{
    using Redpoint.Uefs.Protocol;

    public interface ICredentialDiscovery
    {
        GitCredential GetGitCredential(string repositoryUrl);

        RegistryCredential GetRegistryCredential(string containerRegistryTag);
    }
}