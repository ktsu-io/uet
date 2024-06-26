﻿namespace Redpoint.OpenGE.Component.Dispatcher.WorkerPool
{
    public class WorkerCoreRequestStatistics
    {
        public required int UnfulfilledLocalRequests { get; init; }
        public required int UnfulfilledRemotableRequests { get; init; }
        public required int FulfilledLocalRequests { get; init; }
        public required int FulfilledRemotableRequests { get; init; }
    }
}
