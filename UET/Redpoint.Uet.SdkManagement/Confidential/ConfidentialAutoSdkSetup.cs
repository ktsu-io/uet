namespace Redpoint.Uet.SdkManagement
{
    using Microsoft.Extensions.Logging;
    using Redpoint.ProcessExecution;
    using Redpoint.Uet.Core;
    using System.Runtime.Versioning;
    using System.Threading;
    using System.Threading.Tasks;

    [SupportedOSPlatform("windows")]
    public class ConfidentialAutoSdkSetup : ConfidentialSdkSetup, IAutoSdkSetup
    {
        public ConfidentialAutoSdkSetup(
            string platformName,
            ConfidentialPlatformConfig config,
            IProcessExecutor processExecutor,
            ILogger<ConfidentialAutoSdkSetup> logger,
            IStringUtilities stringUtilities) : base(
                platformName,
                config,
                processExecutor,
                logger,
                stringUtilities)
        {
        }

        public Task<AutoSdkMapping[]> GetAutoSdkMappingsForSdkPackage(string sdkPackagePath, CancellationToken cancellationToken)
        {
            var mappings = _config.AutoSdkRelativePathMappings ?? new Dictionary<string, string>();
            return Task.FromResult(mappings.Select(x => new AutoSdkMapping
            {
                RelativePathInsideAutoSdkPath = x.Key,
                RelativePathInsideSdkPackagePath = x.Value
            }).ToArray());
        }
    }
}
