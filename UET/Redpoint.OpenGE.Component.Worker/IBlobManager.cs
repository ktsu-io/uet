﻿namespace Redpoint.OpenGE.Component.Worker
{
    using Grpc.Core;
    using Redpoint.OpenGE.Protocol;
    using System.Threading.Tasks;

    internal interface IBlobManager
    {
        void NotifyServerCallEnded(ServerCallContext context);

        string ConvertAbsolutePathToBuildDirectoryPath(
            string targetDirectory, 
            string absolutePath);

        Task LayoutBuildDirectoryAsync(
            string targetDirectory,
            InputFilesByBlobXxHash64 inputFiles,
            string virtualRootPath,
            CancellationToken cancellationToken);

        Task QueryMissingBlobsAsync(
            ServerCallContext context,
            QueryMissingBlobsRequest request,
            IServerStreamWriter<ExecutionResponse> responseStream,
            CancellationToken cancellationToken);

        Task SendCompressedBlobsAsync(
            ServerCallContext context,
            ExecutionRequest initialRequest,
            IAsyncStreamReader<ExecutionRequest> requestStream,
            IServerStreamWriter<ExecutionResponse> responseStream,
            CancellationToken cancellationToken);
    }
}
