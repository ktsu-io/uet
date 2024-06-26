﻿namespace UET.Commands.Internal.TestUefsConnection
{
    using Microsoft.Extensions.Logging;
    using Redpoint.Uefs.Protocol;
    using System.CommandLine;
    using System.CommandLine.Invocation;
    using System.Threading.Tasks;
    using static Redpoint.Uefs.Protocol.Uefs;

    internal sealed class TestUefsConnectionCommand
    {
        internal sealed class Options
        {
        }

        public static Command CreateTestUefsConnectionCommand()
        {
            var options = new Options();
            var command = new Command("test-uefs-connection");
            command.AddAllOptions(options);
            command.AddCommonHandler<TestUefsConnectionCommandInstance>(options);
            return command;
        }

        private sealed class TestUefsConnectionCommandInstance : ICommandInstance
        {
            private readonly ILogger<TestUefsConnectionCommandInstance> _logger;
            private readonly UefsClient _uefsClient;

            public TestUefsConnectionCommandInstance(
                ILogger<TestUefsConnectionCommandInstance> logger,
                UefsClient uefsClient)
            {
                _logger = logger;
                _uefsClient = uefsClient;
            }

            public async Task<int> ExecuteAsync(InvocationContext context)
            {
                var results = await _uefsClient.ListAsync(new ListRequest());
                if (!string.IsNullOrWhiteSpace(results.Err))
                {
                    _logger.LogError($"Error while listing UEFS mounts: {results.Err}");
                    return 1;
                }
                _logger.LogInformation($"There are {results.Mounts.Count} mounts on this system.");
                foreach (var result in results.Mounts)
                {
                    _logger.LogInformation($" - {result.Id}");
                }
                return 0;
            }
        }
    }
}
