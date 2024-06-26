﻿namespace Redpoint.GrpcPipes
{
    using Microsoft.Extensions.Logging;

    internal static partial class GrpcPipeLog
    {
        [LoggerMessage(
            EventId = 0,
            Level = LogLevel.Trace,
            Message = "Attempting to start gRPC server...")]
        public static partial void GrpcServerStarting(ILogger logger);

        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Trace,
            Message = "Using TCP socket with plain text pointer file to workaround issue in .NET 7 where Unix sockets do not work on Windows.")]
        public static partial void TcpSocketFallback(ILogger logger);

        [LoggerMessage(
            EventId = 2,
            Level = LogLevel.Trace,
            Message = "Wrote pointer file with content '{pointerContent}' to: {pipePath}")]
        public static partial void WrotePointerFile(ILogger logger, string pointerContent, string pipePath);

        [LoggerMessage(
            EventId = 3,
            Level = LogLevel.Trace,
            Message = "gRPC server started successfully.")]
        public static partial void GrpcServerStarted(ILogger logger);

        [LoggerMessage(
            EventId = 4,
            Level = LogLevel.Trace,
            Message = "Removing existing pointer file from: {pipePath}")]
        public static partial void RemovingPointerFile(ILogger logger, string pipePath);

        [LoggerMessage(
            EventId = 5,
            Level = LogLevel.Trace,
            Message = "Removing existing UNIX socket from: {pipePath}")]
        public static partial void RemovingUnixSocket(ILogger logger, string pipePath);
    }
}