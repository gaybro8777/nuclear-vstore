using Prometheus;

namespace NuClear.VStore.Prometheus
{
    public static class Labels
    {
        public static class Backends
        {
            public const string Aws = "aws";
            public const string Ceph = "ceph";
        }
    }

    public sealed class MetricsProvider
    {
        private const string JoinSeparator = "_";

        private readonly Histogram _requestDurationMs =
            Metrics.CreateHistogram(
                string.Join(JoinSeparator, Names.RequestDurationMetric, NonBaseUnits.Milliseconds),
                "Request duration in milliseconds",
                new HistogramConfiguration
                    {
                        Buckets = new double[] { 5, 10, 50, 100, 150, 200, 250, 300, 400, 500, 800, 1000, 1500, 2000, 5000, 10000, 15000, 20000 },
                        LabelNames = new[]
                            {
                                Names.BackendLabel,
                                Names.TypeLabel,
                                Names.MethodLabel
                            }
                    });

        private readonly Histogram _fetchDurationMs =
            Metrics.CreateHistogram(
                string.Join(JoinSeparator, Names.FetchDurationMetric, NonBaseUnits.Milliseconds),
                "Fetch request duration in milliseconds",
                new HistogramConfiguration
                    {
                        Buckets =
                            new double[] { 5, 10, 50, 100, 150, 200, 250, 300, 400, 500, 800, 1000, 1500, 2000, 5000, 10000, 15000, 20000 },
                        LabelNames = new[] { Names.HostLabel }
                    });

        private readonly Counter _requestErrors =
            Metrics.CreateCounter(Names.RequestErrorsMetric, "Request errors count", Names.BackendLabel, Names.TypeLabel, Names.MethodLabel);

        private readonly Counter _fetchErrors =
            Metrics.CreateCounter(Names.FetchErrorsMetric, "Fetch errors count");

        private readonly Counter _fetchRetries =
            Metrics.CreateCounter(Names.FetchRetriesMetric, "Fetch retries count");

        private readonly Counter _interruptedFetchRequests =
            Metrics.CreateCounter(Names.InterruptedFetchRequestsMetric, "Interrupted fetch requests count");

        private readonly Counter _uploadedBinaries =
            Metrics.CreateCounter(Names.UploadedBinariesMetric, "Uploaded binaries count");

        private readonly Counter _referencedBinaries =
            Metrics.CreateCounter(Names.ReferencedBinariesMetric, "Referenced binaries count");

        private readonly Counter _removedBinaries =
            Metrics.CreateCounter(Names.RemovedBinariesMetric, "Removed binaries count");

        private readonly Counter _createdSessions =
            Metrics.CreateCounter(Names.CreatedSessionsMetric, "Created sessions count");

        private readonly Counter _removedSessions =
            Metrics.CreateCounter(Names.RemovedSessionsMetric, "Removed sessions count");

        public Histogram GetRequestDurationMsMetric() => _requestDurationMs;

        public Histogram GetFetchDurationMsMetric() => _fetchDurationMs;

        public Counter GetRequestErrorsMetric() => _requestErrors;

        public Counter GetUploadedBinariesMetric() => _uploadedBinaries;

        public Counter GetReferencedBinariesMetric() => _referencedBinaries;

        public Counter GetRemovedBinariesMetric() => _removedBinaries;

        public Counter GetCreatedSessionsMetric() => _createdSessions;

        public Counter GetRemovedSessionsMetric() => _removedSessions;

        public Counter GetFetchErrorsMetric() => _fetchErrors;

        public Counter GetFetchRetriesMetric() => _fetchRetries;

        public Counter GetInterruptedFetchRequestsMetric() => _interruptedFetchRequests;

        private static class Names
        {
            public const string RequestDurationMetric = "vstore_request_duration";
            public const string FetchDurationMetric = "vstore_fetch_duration";
            public const string RequestErrorsMetric = "vstore_request_errors";
            public const string FetchErrorsMetric = "vstore_fetch_errors";
            public const string FetchRetriesMetric = "vstore_fetch_retries";
            public const string InterruptedFetchRequestsMetric = "vstore_fetch_requests_interrupted";
            public const string UploadedBinariesMetric = "vstore_binaries_uploaded";
            public const string ReferencedBinariesMetric = "vstore_binaries_referenced";
            public const string RemovedBinariesMetric = "vstore_binaries_removed";
            public const string CreatedSessionsMetric = "vstore_sessions_created";
            public const string RemovedSessionsMetric = "vstore_sessions_removed";
            public const string BackendLabel = "backend";
            public const string TypeLabel = "type";
            public const string MethodLabel = "method";
            public const string HostLabel = "host";
        }

        private static class NonBaseUnits
        {
            public const string Milliseconds = "milliseconds";
        }
    }
}
