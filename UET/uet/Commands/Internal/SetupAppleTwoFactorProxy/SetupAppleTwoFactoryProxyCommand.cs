namespace UET.Commands.Internal.SetupAppleTwoFactorProxy
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;
    using System.CommandLine;
    using System.CommandLine.Invocation;
    using Microsoft.Extensions.Logging;
    using System.Net.Http.Headers;
    using System.Text.Json.Serialization.Metadata;
    using System.Net.Http.Json;
    using System.Runtime.CompilerServices;
    using System.Net.Mime;

    internal partial class SetupAppleTwoFactoryProxyCommand
    {
        public class Options
        {
            public Option<string> CloudflareApiToken = new Option<string>("--cloudflare-api-token") { IsRequired = true };
            public Option<string> CloudflareAccountId = new Option<string>("--cloudflare-account-id") { IsRequired = true };
            public Option<string> PlivoAuthId = new Option<string>("--plivo-auth-id") { IsRequired = true };
            public Option<string> PlivoAuthToken = new Option<string>("--plivo-auth-token") { IsRequired = true };
        }

        public static Command CreateSetupAppleTwoFactoryProxyCommand()
        {
            var options = new Options();
            var command = new Command("setup-apple-two-factor-proxy");
            command.AddAllOptions(options);
            command.AddCommonHandler<SetupAppleTwoFactoryProxyCommandInstance>(options);
            return command;
        }

        public class SetupAppleTwoFactoryProxyCommandInstance : ICommandInstance
        {
            private readonly ILogger<SetupAppleTwoFactoryProxyCommandInstance> _logger;
            private readonly Options _options;

            public SetupAppleTwoFactoryProxyCommandInstance(
                ILogger<SetupAppleTwoFactoryProxyCommandInstance> logger,
                Options options)
            {
                _logger = logger;
                _options = options;
            }

            public async Task<int> ExecuteAsync(InvocationContext context)
            {
                var cloudflareApiToken = context.ParseResult.GetValueForOption(_options.CloudflareApiToken)!;
                var cloudflareAccountId = context.ParseResult.GetValueForOption(_options.CloudflareAccountId)!;
                var plivoAuthId = context.ParseResult.GetValueForOption(_options.PlivoAuthId)!;
                var plivoAuthToken = context.ParseResult.GetValueForOption(_options.PlivoAuthToken)!;

                using (var cfClient = new HttpClient())
                {
                    cfClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cloudflareApiToken);

                    await foreach (var worker in FetchCloudflareListAsync(
                        cfClient,
                        $"https://api.cloudflare.com/client/v4/accounts/{cloudflareAccountId}/workers/scripts",
                        CloudflareJsonSerializerContext.Default.CloudflareListCloudflareWorker,
                        context.GetCancellationToken()))
                    {
                        _logger.LogInformation(worker.Id!);
                    }
                }

                using (var plivoClient = new HttpClient())
                {
                    var plivoBasic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{plivoAuthId}:{plivoAuthToken}"));
                    plivoClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", plivoBasic);

                    // See if we have an application for the 2FA proxy.
                    await foreach (var application in FetchPlivoListAsync(
                        plivoClient,
                        $"https://api.plivo.com/v1/Account/{plivoAuthId}/Application/",
                        PlivoJsonSerializerContext.Default.PlivoListPlivoApplication,
                        context.GetCancellationToken()))
                    {
                        _logger.LogInformation(application.AppName);
                    }
                }

                return 0;
            }

            private async IAsyncEnumerable<T> FetchCloudflareListAsync<T>(
                HttpClient client,
                string url,
                JsonTypeInfo<CloudflareList<T>> typeInfo,
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                var page = await client.GetFromJsonAsync(
                    url,
                    typeInfo,
                    cancellationToken);
                foreach (var r in page!.Result!)
                {
                    yield return r!;
                }
            }

            private async IAsyncEnumerable<T> FetchPlivoListAsync<T>(
                HttpClient client, 
                string url,
                JsonTypeInfo<PlivoList<T>> typeInfo,
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                var page = await client.GetFromJsonAsync(
                    url, 
                    typeInfo,
                    cancellationToken);
                foreach (var o in page!.Objects!)
                {
                    yield return o;
                }
                while (page!.Meta!.Next != null)
                {
                    var uri = new Uri(url);
                    url = $"{uri.Scheme}://{uri.Host}{page!.Meta!.Next}";
                    page = await client.GetFromJsonAsync(
                        url,
                        typeInfo,
                        cancellationToken);
                    foreach (var o in page!.Objects!)
                    {
                        yield return o;
                    }
                }
            }
        }
    }
}
