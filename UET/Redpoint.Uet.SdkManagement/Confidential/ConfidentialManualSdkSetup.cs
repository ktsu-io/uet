namespace Redpoint.Uet.SdkManagement
{
    using Microsoft.Extensions.Logging;
    using Redpoint.ProcessExecution;
    using Redpoint.Uet.Core;
    using System.Runtime.Versioning;
    using System.Threading;
    using System.Threading.Tasks;

    [SupportedOSPlatform("windows")]
    public class ConfidentialManualSdkSetup : ConfidentialSdkSetup, IManualSdkSetup
    {
        public ConfidentialManualSdkSetup(
            string platformName, 
            ConfidentialPlatformConfig config, 
            IProcessExecutor processExecutor, 
            ILogger<ConfidentialManualSdkSetup> logger, 
            IStringUtilities stringUtilities) : base(
                platformName, 
                config,
                processExecutor, 
                logger, 
                stringUtilities)
        {
        }

        public Task<EnvironmentForSdkUsage> GetRuntimeEnvironmentForSdkPackage(string sdkPackagePath, CancellationToken cancellationToken)
        {
            return Task.FromResult(new EnvironmentForSdkUsage
            {
                EnvironmentVariables = _config.EnvironmentVariables ?? new Dictionary<string, string>(),
            });
        }
    }
}
