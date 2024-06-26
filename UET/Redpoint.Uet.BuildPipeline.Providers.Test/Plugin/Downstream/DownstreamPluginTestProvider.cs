﻿namespace Redpoint.Uet.BuildPipeline.Providers.Test.Plugin.Downstream
{
    using Redpoint.RuntimeJson;
    using Redpoint.Uet.BuildGraph;
    using Redpoint.Uet.BuildPipeline.Providers.Test.Plugin.Custom;
    using Redpoint.Uet.Configuration;
    using Redpoint.Uet.Configuration.Dynamic;
    using Redpoint.Uet.Configuration.Plugin;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Text.Json.Serialization.Metadata;
    using System.Threading.Tasks;
    using System.Xml;

    internal sealed class DownstreamPluginTestProvider : IPluginTestProvider
    {
        private readonly IGlobalArgsProvider? _globalArgsProvider;

        public DownstreamPluginTestProvider(IGlobalArgsProvider? globalArgsProvider = null)
        {
            _globalArgsProvider = globalArgsProvider;
        }

        public string Type => "Downstream";

        public IRuntimeJson DynamicSettings { get; } = new TestProviderRuntimeJson(TestProviderSourceGenerationContext.WithStringEnum).BuildConfigPluginTestDownstream;

        public async Task WriteBuildGraphNodesAsync(
            IBuildGraphEmitContext context,
            XmlWriter writer,
            BuildConfigPluginDistribution buildConfigDistribution,
            IEnumerable<BuildConfigDynamic<BuildConfigPluginDistribution, ITestProvider>> entries)
        {
            var castedEntries = entries
                .Select(x => (name: x.Name, settings: (BuildConfigPluginTestCustom)x.DynamicSettings))
                .ToList();

            foreach (var entry in castedEntries)
            {
                var nodeName = $"Downstream {entry.name}";

                await writer.WriteAgentNodeAsync(
                    new AgentNodeElementProperties
                    {
                        AgentStage = $"Downstream Tests",
                        AgentType = "Meta",
                        NodeName = nodeName,
                        Requires = "#PackagedPlugin"
                    },
                    async writer =>
                    {
                        await writer.WriteSpawnAsync(
                            new SpawnElementProperties
                            {
                                Exe = "$(UETPath)",
                                Arguments = (_globalArgsProvider?.GlobalArgsArray ?? Array.Empty<string>()).Concat(new[]
                                {
                                    "internal",
                                    "run-downstream-test",
                                    "--downstream-test",
                                    $@"""{entry.name}""",
                                    "--engine-path",
                                    $@"""$(EnginePath)""",
                                    "--distribution",
                                    $@"""$(Distribution)""",
                                    "--packaged-plugin-path",
                                    $@"""$(TempPath)/$(PackageFolder)/""",
                                }).ToArray()
                            }).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                await writer.WriteDynamicNodeAppendAsync(
                    new DynamicNodeAppendElementProperties
                    {
                        NodeName = nodeName,
                        MustPassForLaterDeployment = true,
                    }).ConfigureAwait(false);
            }
        }
    }
}