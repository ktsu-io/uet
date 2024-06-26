﻿namespace Redpoint.Uet.Configuration.Dynamic
{
    using Redpoint.RuntimeJson;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Text.Json.Serialization.Metadata;
    using System.Threading.Tasks;
    using System.Xml;

    public interface IDynamicProvider
    {
        IRuntimeJson DynamicSettings { get; }
    }

    public interface IDynamicProvider<TDistribution, TBaseClass> : IDynamicProviderRegistration, IDynamicProvider
    {
        /// <summary>
        /// Writes the build graph nodes for all of the elements of the same type.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="writer">The XML writer.</param>
        /// <param name="buildConfigDistribution">The build distribution.</param>
        /// <param name="entries">The list of entries to process.</param>
        /// <returns>An awaitable task.</returns>
        Task WriteBuildGraphNodesAsync(
            IBuildGraphEmitContext context,
            XmlWriter writer,
            TDistribution buildConfigDistribution,
            IEnumerable<BuildConfigDynamic<TDistribution, TBaseClass>> entries);
    }
}
