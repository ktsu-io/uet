namespace UET.Commands.Internal.SetupAppleTwoFactorProxy
{
    using System.Text.Json.Serialization;

    [JsonSerializable(typeof(CloudflareList<CloudflareWorker>))]
    internal partial class CloudflareJsonSerializerContext : JsonSerializerContext
    {
    }
}
