﻿namespace Redpoint.CredentialDiscovery
{
    using Redpoint.Uefs.Protocol;
    using System.Text.Json;
    using System.Text;
    using System.Text.RegularExpressions;
    using Redpoint.ThirdParty.CredentialManagement;

    internal class DefaultCredentialDiscovery : ICredentialDiscovery
    {
        private static readonly Regex _tagRegex = new Regex("^(?<host>[a-z\\.\\:0-9]+)/(?<path>[a-z\\./0-9-_]+):?(?<label>[a-z\\.0-9-_]+)?$");

        public GitCredential GetGitCredential(string repositoryUrl)
        {
            var shortSshUrlRegex = new Regex("^(.+@)*([\\w\\d\\.]+):(.*)$");
            if (!repositoryUrl.Contains("://"))
            {
                var shortSshUrlMatch = shortSshUrlRegex.Match(repositoryUrl);
                if (shortSshUrlMatch.Success)
                {
                    repositoryUrl = $"ssh://{shortSshUrlMatch.Groups[1].Value}{shortSshUrlMatch.Groups[2].Value}/{shortSshUrlMatch.Groups[3].Value}";
                }
            }

            var repositoryUri = new Uri(repositoryUrl);
            if (repositoryUri.Scheme.Equals("http", StringComparison.InvariantCultureIgnoreCase) ||
                repositoryUri.Scheme.Equals("https", StringComparison.InvariantCultureIgnoreCase))
            {
                var s = repositoryUri.UserInfo.Split(':', 2);
                if (s.Length == 2)
                {
                    return new GitCredential
                    {
                        Username = s[0],
                        Password = s[1],
                    };
                }
                else
                {
                    var envUsername = Environment.GetEnvironmentVariable($"REDPOINT_CREDENTIAL_DISCOVERY_USERNAME_{repositoryUri.Host}");
                    var envPassword = Environment.GetEnvironmentVariable($"REDPOINT_CREDENTIAL_DISCOVERY_PASSWORD_{repositoryUri.Host}");
                    if (!string.IsNullOrWhiteSpace(envUsername) && !string.IsNullOrWhiteSpace(envPassword))
                    {
                        return new GitCredential
                        {
                            Username = envUsername,
                            Password = envPassword,
                        };
                    }

                    throw new UnableToDiscoverCredentialException($"The HTTP/S URL for Git did not contain a username and password, and the environment variables 'REDPOINT_CREDENTIAL_DISCOVERY_USERNAME_{repositoryUri.Host}' / 'REDPOINT_CREDENTIAL_DISCOVERY_PASSWORD_{repositoryUri.Host}' were not set.");
                }
            }
            else if (repositoryUri.Scheme.Equals("ssh", StringComparison.InvariantCultureIgnoreCase))
            {
                var envPrivateKeyPath = Environment.GetEnvironmentVariable($"REDPOINT_CREDENTIAL_DISCOVERY_SSH_PRIVATE_KEY_PATH_{repositoryUri.Host}");
                var envPublicKeyPath = Environment.GetEnvironmentVariable($"REDPOINT_CREDENTIAL_DISCOVERY_SSH_PUBLIC_KEY_PATH_{repositoryUri.Host}");
                var envPrivateKey = Environment.GetEnvironmentVariable($"REDPOINT_CREDENTIAL_DISCOVERY_SSH_PRIVATE_KEY_{repositoryUri.Host}");
                var envPublicKey = Environment.GetEnvironmentVariable($"REDPOINT_CREDENTIAL_DISCOVERY_SSH_PUBLIC_KEY_{repositoryUri.Host}");
                if (!string.IsNullOrWhiteSpace(envPrivateKeyPath) && !string.IsNullOrWhiteSpace(envPublicKeyPath))
                {
                    if (File.Exists(envPrivateKeyPath) && File.Exists(envPublicKeyPath))
                    {
                        return new GitCredential
                        {
                            SshPublicKeyAsPem = File.ReadAllText(envPrivateKeyPath).Trim(),
                            SshPrivateKeyAsPem = File.ReadAllText(envPublicKeyPath).Trim(),
                        };
                    }
                    else
                    {
                        throw new UnableToDiscoverCredentialException($"The private or public key specified in 'REDPOINT_CREDENTIAL_DISCOVERY_SSH_PRIVATE_KEY_PATH_{repositoryUri.Host}' / 'REDPOINT_CREDENTIAL_DISCOVERY_SSH_PUBLIC_KEY_PATH_{repositoryUri.Host}' does not exist on disk.");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(envPrivateKey) && !string.IsNullOrWhiteSpace(envPublicKey))
                {
                    return new GitCredential
                    {
                        SshPublicKeyAsPem = envPrivateKey.Trim(),
                        SshPrivateKeyAsPem = envPublicKey.Trim(),
                    };
                }

                var publicKeyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_rsa.pub");
                var privateKeyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_rsa");
                if (!File.Exists(publicKeyFile) || !File.Exists(privateKeyFile))
                {
                    throw new UnableToDiscoverCredentialException($"The private or public key for SSH did not exist at '{publicKeyFile}' and {privateKeyFile}', and none of the environment variables 'REDPOINT_CREDENTIAL_DISCOVERY_SSH_PRIVATE_KEY_PATH_{repositoryUri.Host}', 'REDPOINT_CREDENTIAL_DISCOVERY_SSH_PUBLIC_KEY_PATH_{repositoryUri.Host}', 'REDPOINT_CREDENTIAL_DISCOVERY_SSH_PRIVATE_KEY_{repositoryUri.Host}' or 'REDPOINT_CREDENTIAL_DISCOVERY_SSH_PUBLIC_KEY_{repositoryUri.Host}' were specified.");
                }

                return new GitCredential
                {
                    SshPublicKeyAsPem = File.ReadAllText(publicKeyFile).Trim(),
                    SshPrivateKeyAsPem = File.ReadAllText(privateKeyFile).Trim(),
                };
            }
            else
            {
                throw new UnableToDiscoverCredentialException($"The scheme '{repositoryUri.Scheme}' is unsupported for Git repositories.");
            }
        }

        public RegistryCredential GetRegistryCredential(string containerRegistryTag)
        {
            var tag = _tagRegex.Match(containerRegistryTag);
            if (!tag.Success)
            {
                throw new InvalidOperationException("Invalid package URL");
            }
            var host = tag.Groups["host"].Value;

            var ciRegistry = Environment.GetEnvironmentVariable("CI_REGISTRY");
            var ciRegistryUser = Environment.GetEnvironmentVariable("CI_REGISTRY_USER");
            var ciRegistryPassword = Environment.GetEnvironmentVariable("CI_REGISTRY_PASSWORD");

            if (!string.IsNullOrWhiteSpace(ciRegistry) && !string.IsNullOrWhiteSpace(ciRegistryUser) && !string.IsNullOrWhiteSpace(ciRegistryPassword))
            {
                if (string.Compare(ciRegistry, host, true) == 0)
                {
                    // If the GitLab CI variables are for this registry host, just use the environment variables directly.
                    // This saves you from having to run `docker login` (which is known to have issues if run concurrently
                    // on the same machine).
                    return new RegistryCredential
                    {
                        Username = ciRegistryUser,
                        Password = ciRegistryPassword,
                    };
                }
            }

            var dockerJsonPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".docker",
                "config.json");
            if (!File.Exists(dockerJsonPath))
            {
                throw new InvalidOperationException("Missing Docker CLI configuration, which is necessary to authenticate with package registries.");
            }
            var dockerConfig = JsonSerializer.Deserialize<DockerConfigJson>(File.ReadAllText(dockerJsonPath), DockerConfigJsonSourceGenerationContext.Default.DockerConfigJson);

            if (dockerConfig?.CredsStore == "wincred")
            {
                var credential = new Credential
                {
                    Target = host
                };
                if (!credential.Load())
                {
                    throw new InvalidOperationException($"Unable to access the credential stored in the Windows Credential Manager for '{host}'.");
                }
                var password = Encoding.UTF8.GetString(Encoding.Unicode.GetBytes(credential.Password));
                return new RegistryCredential
                {
                    Username = credential.Username,
                    Password = password,
                };
            }
            else if (dockerConfig?.Auths?.ContainsKey(host) ?? false)
            {
                var basicAuth = Encoding.UTF8.GetString(Convert.FromBase64String(dockerConfig.Auths[host].Auth)).Split(":", 2);
                return new RegistryCredential
                {
                    Username = basicAuth[0],
                    Password = basicAuth[1],
                };
            }
            else
            {
                throw new InvalidOperationException("Unsupported credential storage type, or we don't have credentials for this registry.");
            }
        }
    }
}