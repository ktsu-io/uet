namespace Redpoint.Uet.SdkManagement
{
    public interface IAutoSdkSetup : ISdkSetup
    {
        Task<AutoSdkMapping[]> GetAutoSdkMappingsForSdkPackage(string sdkPackagePath, CancellationToken cancellationToken);
    }
}