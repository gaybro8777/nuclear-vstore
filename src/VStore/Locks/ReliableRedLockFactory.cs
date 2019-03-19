using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using NuClear.VStore.Options;

using Polly;
using Polly.Retry;

using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;

using StackExchange.Redis;

namespace NuClear.VStore.Locks
{
    public sealed class ReliableRedLockFactory : IDistributedLockFactory, IDisposable
    {
        private const int DefaultConnectionTimeout = 1000;
        private const int DefaultSyncTimeout = 1000;
        private const int DefaultKeepAlive = 1;
        private const int RetryCount = 3;

        private static readonly TimeSpan RetryTimeout = TimeSpan.FromMilliseconds(100);

        private static readonly RetryPolicy<RedLockFactory> WaitPolicy =
            Policy.HandleResult<RedLockFactory>(factory => factory == null)
                  .WaitAndRetryForever(attempt => RetryTimeout);

        private static readonly RetryPolicy RetryPolicy =
            Policy.Handle<Exception>(ex => !(ex is LockAlreadyExistsException))
                  .WaitAndRetry(RetryCount, attempt => RetryTimeout);

        private static readonly AsyncRetryPolicy RetryPolicyAsync =
            Policy.Handle<Exception>(ex => !(ex is LockAlreadyExistsException))
                  .WaitAndRetryAsync(RetryCount, attempt => RetryTimeout);

        private static ILogger<ConnectionMultiplexer> _logger;

        private readonly DistributedLockOptions _lockOptions;
        private readonly ILoggerFactory _loggerFactory;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly DnsEndPoint[] _endPoints;

        private RedLockFactory _innerFactory;
        private IConnectionMultiplexer[] _multiplexers;

        public ReliableRedLockFactory(DistributedLockOptions lockOptions, ILoggerFactory loggerFactory)
        {
            _lockOptions = lockOptions;
            _loggerFactory = loggerFactory;

            _logger = _loggerFactory.CreateLogger<ConnectionMultiplexer>();

            _endPoints = lockOptions.GetEndPoints().Select(x => new DnsEndPoint(x.Host, x.Port)).ToArray();
            if (_endPoints == null || !_endPoints.Any())
            {
                throw new ArgumentException("No endpoints specified.");
            }

            RunRedLockFactoryResilienceTask();
        }

        public IRedLock CreateLock(string resource, TimeSpan expiryTime)
        {
            WaitPolicy.Execute(() => _innerFactory);
            var redLock = RetryPolicy.Execute(() => _innerFactory.CreateLock(resource, expiryTime));

            return new SafeRedLock(_logger, redLock);
        }

        public async Task<IRedLock> CreateLockAsync(string resource, TimeSpan expiryTime)
        {
            WaitPolicy.Execute(() => _innerFactory);
            var redLock = await RetryPolicyAsync.ExecuteAsync(() => _innerFactory.CreateLockAsync(resource, expiryTime));

            return new SafeRedLock(_logger, redLock);
        }

        public IRedLock CreateLock(
            string resource,
            TimeSpan expiryTime,
            TimeSpan waitTime,
            TimeSpan retryTime,
            CancellationToken? cancellationToken = null)
        {
            WaitPolicy.Execute(() => _innerFactory);
            var redLock = RetryPolicy.Execute(() => _innerFactory.CreateLock(resource, expiryTime, waitTime, retryTime, cancellationToken));

            return new SafeRedLock(_logger, redLock);
        }

        public async Task<IRedLock> CreateLockAsync(
            string resource,
            TimeSpan expiryTime,
            TimeSpan waitTime,
            TimeSpan retryTime,
            CancellationToken? cancellationToken = null)
        {
            WaitPolicy.Execute(() => _innerFactory);
            var redLock = await RetryPolicyAsync.ExecuteAsync(() => _innerFactory.CreateLockAsync(resource, expiryTime, waitTime, retryTime, cancellationToken));

            return new SafeRedLock(_logger, redLock);
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();

            foreach (var multiplexer in _multiplexers)
            {
                multiplexer.Dispose();
            }

            _innerFactory?.Dispose();
        }

        private static void OnConnectionFailed(object sender, ConnectionFailedEventArgs args)
            => _logger.LogWarning(
                args.Exception,
                "ConnectionFailed: {endpoint} ConnectionType: {connectionType} FailureType: {failureType}",
                GetFriendlyName(args.EndPoint),
                args.ConnectionType,
                args.FailureType);

        private static void OnConnectionRestored(object sender, ConnectionFailedEventArgs args)
            => _logger.LogWarning(
                args.Exception,
                "ConnectionRestored: {endpoint} ConnectionType: {connectionType} FailureType: {failureType}",
                GetFriendlyName(args.EndPoint),
                args.ConnectionType,
                args.FailureType);

        private static void OnInternalError(object sender, InternalErrorEventArgs args)
            => _logger.LogWarning(
                args.Exception,
                "InternalError: {endpoint} ConnectionType: {connectionType} Origin: {origin}",
                GetFriendlyName(args.EndPoint),
                args.ConnectionType,
                args.Origin);

        private static void OnErrorMessage(object sender, RedisErrorEventArgs args)
            => _logger.LogWarning("ErrorMessage: {endpoint} Message: {message}", GetFriendlyName(args.EndPoint), args.Message);

        private static string GetFriendlyName(EndPoint endPoint)
        {
            switch (endPoint)
            {
                case DnsEndPoint dnsEndPoint:
                    return $"{dnsEndPoint.Host}:{dnsEndPoint.Port}";
                case IPEndPoint ipEndPoint:
                    return $"{ipEndPoint.Address}:{ipEndPoint.Port}";
                default:
                    return endPoint.ToString();
            }
        }

        private static void DisposeMultiplexers(ILogger logger, IEnumerable<IConnectionMultiplexer> multiplexers)
        {
            foreach (var multiplexer in multiplexers)
            {
                var endpoint = multiplexer.GetEndPoints()[0];
                logger.LogTrace("Disposing connection to endpoint at {endpoint}...", GetFriendlyName(endpoint));

                multiplexer.ConnectionFailed -= OnConnectionFailed;
                multiplexer.ConnectionRestored -= OnConnectionRestored;
                multiplexer.InternalError -= OnInternalError;
                multiplexer.ErrorMessage -= OnErrorMessage;
                multiplexer.Dispose();
            }
        }

        private async Task<(int, IConnectionMultiplexer[])> Initialize()
        {
            var multiplexers = new IConnectionMultiplexer[_endPoints.Length];
            var keepAlive = _lockOptions.KeepAlive ?? DefaultKeepAlive;
            var logWriter = new LogWriter(_logger);

            var tasks = _endPoints.Select(
                async (endpoint, index) =>
                    {
                        var redisConfig = new ConfigurationOptions
                            {
                                DefaultVersion = new Version(4, 0),
                                AbortOnConnectFail = false,
                                EndPoints = { endpoint },
                                CommandMap = CommandMap.Create(new HashSet<string> { "SUBSCRIBE" }, available: false),
                                Password = _lockOptions.Password,
                                ConnectTimeout = _lockOptions.ConnectionTimeout ?? DefaultConnectionTimeout,
                                SyncTimeout = _lockOptions.SyncTimeout ?? DefaultSyncTimeout,
                                KeepAlive = keepAlive,

                                // Time (seconds) to check configuration. This serves as a keep-alive for interactive sockets, if it is supported.
                                ConfigCheckSeconds = keepAlive
                            };

                        var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConfig, logWriter);
                        multiplexer.ConnectionFailed += OnConnectionFailed;
                        multiplexer.ConnectionRestored += OnConnectionRestored;
                        multiplexer.InternalError += OnInternalError;
                        multiplexer.ErrorMessage += OnErrorMessage;

                        multiplexers[index] = multiplexer;

                        _logger.LogInformation(
                            "Tried to connect to RedLock endpoint at {host}:{port}. Connection status: {connectionStatus}",
                            endpoint.Host,
                            endpoint.Port,
                            multiplexer.IsConnected ? "active" : "disconnected");
                    });

            await Task.WhenAll(tasks);

            return (keepAlive, multiplexers);
        }

        private void RunRedLockFactoryResilienceTask()
        {
            Task.Factory.StartNew(
                async () =>
                    {
                        var logger = _loggerFactory.CreateLogger<ReliableRedLockFactory>();
                        var timeout = DefaultKeepAlive;

                        var recreationNeeded = true;
                        while (!_cancellationTokenSource.IsCancellationRequested)
                        {
                            if (recreationNeeded)
                            {
                                try
                                {
                                    var (keepAlive, multiplexers) = await Initialize();
                                    timeout = keepAlive;

                                    var redLockMultiplexers = multiplexers.Select(x => new RedLockMultiplexer(x)).ToList();
                                    var factory = RedLockFactory.Create(redLockMultiplexers, _loggerFactory);

                                    if (_innerFactory == null)
                                    {
                                        _multiplexers = multiplexers;
                                        _innerFactory = factory;
                                    }
                                    else
                                    {
                                        var oldMultiplexers = Interlocked.Exchange(ref _multiplexers, multiplexers);
                                        var oldFactory = Interlocked.Exchange(ref _innerFactory, factory);

                                        DisposeMultiplexers(logger, oldMultiplexers);

                                        oldFactory.Dispose();
                                    }

                                    var nonConnected = multiplexers.Count(x => !x.IsConnected);
                                    logger.LogInformation(
                                        "Successfully connected to {count} of {totalCount} RedLock endpoints. RedLock factory has been replaced with new connections.",
                                        _multiplexers.Length - nonConnected,
                                        _multiplexers.Length);

                                    recreationNeeded = false;
                                }
                                catch (Exception ex)
                                {
                                    logger.LogError(
                                        ex,
                                        "Unexpected error occured while connecting to RedLock endpoints. Will try again in {timeout} seconds.",
                                        timeout);
                                }
                            }

                            for (var index = 0; index < _multiplexers.Length; index++)
                            {
                                var endpoint = _endPoints[index];
                                var multiplexer = _multiplexers[index];
                                try
                                {
                                    logger.LogTrace("Checking RedLock endpoint {endpoint} for availablity.", GetFriendlyName(endpoint));
                                    var server = multiplexer.GetServer(endpoint);
                                    var latency = server.Ping();
                                    logger.LogTrace("RedLock endpoint {endpoint} is available. Latency: {latency}", GetFriendlyName(endpoint), latency);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogWarning(
                                        "RedLock endpoint {endpoint} is unavailable. All connections will be recreated. Exception: {exception}",
                                        GetFriendlyName(endpoint),
                                        ex.ToString());
                                    recreationNeeded = true;
                                }
                            }

                            await Task.Delay(TimeSpan.FromSeconds(timeout));
                        }
                    },
                _cancellationTokenSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        private class LogWriter : TextWriter
        {
            private readonly ILogger _logger;

            public LogWriter(ILogger logger)
            {
                _logger = logger;
            }

            public override Encoding Encoding => Encoding.UTF8;

            public override void WriteLine(string value) => _logger.LogTrace(value);
            public override void WriteLine(string format, object arg0) => _logger.LogTrace(format, arg0);
            public override void WriteLine(string format, params object[] arg) => _logger.LogTrace(format, arg);
        }
    }
}