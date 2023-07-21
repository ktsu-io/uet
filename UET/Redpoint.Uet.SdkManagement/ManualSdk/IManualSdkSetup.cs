namespace Redpoint.Uet.SdkManagement
{
    public interface IManualSdkSetup : ISdkSetup
    {
        Task<EnvironmentForSdkUsage> GetRuntimeEnvironmentForSdkPackage(string sdkPackagePath, CancellationToken cancellationToken);
    }
}