namespace UET.Commands.Internal.SetupAppleTwoFactorProxy
{
    using System.Text.Json.Serialization;

    internal class CloudflareList<T>
    {
        [JsonPropertyName("result")]
        public T[]? Result { get; set; }
    }
}
