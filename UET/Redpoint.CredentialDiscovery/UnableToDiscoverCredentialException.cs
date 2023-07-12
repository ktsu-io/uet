namespace Redpoint.CredentialDiscovery
{
    public class UnableToDiscoverCredentialException : Exception
    {
        public UnableToDiscoverCredentialException(string? message) : base(message)
        {
        }
    }
}