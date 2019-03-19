using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using NuClear.VStore.Options;
using NuClear.VStore.Prometheus;

using Polly;
using Polly.Timeout;
using Polly.Wrap;

using Prometheus;

namespace NuClear.VStore.Sessions.Fetch
{
    public class FetchClient : IFetchClient
    {
        private const int StreamCopyBufferSize = 81920;

        private readonly int _maxRetryCount;
        private readonly long _maxBinarySize;
        private readonly ILogger<FetchClient> _logger;
        private readonly Counter _fetchErrorsMetric;
        private readonly Counter _fetchRetriesMetric;
        private readonly Histogram _fetchDurationMsMetric;
        private readonly AsyncPolicyWrap _timeoutWithRetryPolicy;
        private readonly Counter _interruptedFetchRequestsMetric;
        private static readonly HttpClient HttpClient = new HttpClient();

        public FetchClient(FetchFileOptions options, MetricsProvider metricsProvider, ILogger<FetchClient> logger)
        {
            _logger = logger;
            _maxRetryCount = options.MaxRetryCount;
            _maxBinarySize = options.MaxBinarySize;
            _fetchErrorsMetric = metricsProvider.GetFetchErrorsMetric();
            _fetchRetriesMetric = metricsProvider.GetFetchRetriesMetric();
            _fetchDurationMsMetric = metricsProvider.GetFetchDurationMsMetric();
            _interruptedFetchRequestsMetric = metricsProvider.GetInterruptedFetchRequestsMetric();
            var timeoutPolicy = Policy.TimeoutAsync(TimeSpan.FromMilliseconds(options.MaxTimeoutMs), TimeoutStrategy.Optimistic);
            var retryPolicy = Policy.Handle<HttpRequestException>()
                                    .Or<TimeoutRejectedException>()
                                    .RetryAsync(_maxRetryCount, RetryHandler);

            _timeoutWithRetryPolicy = retryPolicy.WrapAsync(timeoutPolicy);
        }

        /// <inheritdoc />
        public async Task<(Stream stream, string mediaType)> FetchAsync(Uri fetchUri)
        {
            var stopwatch = Stopwatch.StartNew();
            var policyContext = new Context(fetchUri.ToString());
            _logger.LogDebug("Start fetch request to {url}", fetchUri);
            using (var response = await _timeoutWithRetryPolicy.ExecuteAsync(FetchAction, policyContext, CancellationToken.None))
            {
                stopwatch.Stop();
                _fetchDurationMsMetric.Labels(fetchUri.Host).Observe(stopwatch.ElapsedMilliseconds);
                _logger.LogDebug("Fetch request executed in {elapsedMs} ms", stopwatch.ElapsedMilliseconds);

                if (!response.IsSuccessStatusCode)
                {
                    _fetchErrorsMetric.Inc();
                    var content = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Got status {status} on {method} request to {url} with content: {content}", response.StatusCode, HttpMethod.Get, fetchUri, content);
                    throw new FetchRequestException(response.StatusCode, content);
                }

                if (response.Content.Headers.ContentLength > _maxBinarySize)
                {
                    throw new FetchResponseTooLargeException(response.Content.Headers.ContentLength.Value);
                }

                if (string.IsNullOrEmpty(response.Content.Headers.ContentType.MediaType))
                {
                    throw new FetchResponseContentTypeInvalidException("Content type in response is not specified");
                }

                var memoryStream = new MemoryStream();
                using (var responseStream = await response.Content.ReadAsStreamAsync())
                {
                    int readBytes;
                    var buffer = new byte[StreamCopyBufferSize];
                    do
                    {
                        readBytes = await responseStream.ReadAsync(buffer, 0, buffer.Length);
                        await memoryStream.WriteAsync(buffer, 0, readBytes);
                        if (memoryStream.Length > _maxBinarySize)
                        {
                            throw new FetchResponseTooLargeException(memoryStream.Length);
                        }
                    }
                    while (readBytes > 0);
                }

                return (memoryStream, response.Content.Headers.ContentType.MediaType);
            }
        }

        private static async Task<HttpResponseMessage> FetchAction(Context context, CancellationToken ct)
            => await HttpClient.GetAsync(context.OperationKey, HttpCompletionOption.ResponseHeadersRead, ct);

        private void RetryHandler(Exception ex, int retryCount, Context context)
        {
            _logger.LogWarning(
                "Got an exception while fetching {url}. Retry count: '{retryCount}'. Exception message: {exceptionMessage}",
                context.OperationKey,
                retryCount,
                ex.InnerException?.Message ?? ex.Message);

            _fetchRetriesMetric.Inc();
            if (retryCount == _maxRetryCount)
            {
                _interruptedFetchRequestsMetric.Inc();
            }
        }
    }
}