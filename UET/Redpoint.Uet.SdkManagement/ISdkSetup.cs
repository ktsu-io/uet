namespace Redpoint.Uet.SdkManagement
{
    public interface ISdkSetup
    {
        string[] PlatformNames { get; }

        string CommonPlatformNameForPackageId { get; }

        Task<string> ComputeSdkPackageId(string unrealEnginePath, CancellationToken cancellationToken);

        Task GenerateSdkPackage(string unrealEnginePath, string sdkPackagePath, CancellationToken cancellationToken);   
    }
}