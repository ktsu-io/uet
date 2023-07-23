namespace UET.Commands.Internal.SetupAppleTwoFactorProxy
{
    using System.Text.Json.Serialization;

    [JsonSerializable(typeof(PlivoApplication))]
    [JsonSerializable(typeof(PlivoList<PlivoApplication>))]
    internal partial class PlivoJsonSerializerContext : JsonSerializerContext
    {
    }
}
