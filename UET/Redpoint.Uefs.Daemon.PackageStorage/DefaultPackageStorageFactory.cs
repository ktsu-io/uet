﻿namespace Redpoint.Uefs.Daemon.PackageStorage
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Redpoint.Uefs.Daemon.PackageFs;

    internal sealed class DefaultPackageStorageFactory : IPackageStorageFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public DefaultPackageStorageFactory(
            IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public IPackageStorage CreatePackageStorage(string storagePath)
        {
            return new DefaultPackageStorage(
                _serviceProvider.GetRequiredService<ILogger<DefaultPackageStorage>>(),
                _serviceProvider.GetRequiredService<IPackageFsFactory>(),
                storagePath);
        }
    }
}
