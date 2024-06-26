﻿namespace Redpoint.Uet.Core.Permissions
{
    using System.Threading.Tasks;

    public interface IWorldPermissionApplier
    {
        ValueTask GrantEveryonePermissionAsync(string path, CancellationToken cancellationToken);
    }
}
