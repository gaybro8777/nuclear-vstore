using System;
using System.IO;

using Amazon.Runtime;
using Amazon.S3;

using Autofac;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Newtonsoft.Json.Linq;

using NuClear.VStore.Http;
using NuClear.VStore.Http.Core.Middleware;
using NuClear.VStore.Http.Core.Swashbuckle;
using NuClear.VStore.ImageRendering;
using NuClear.VStore.Locks;
using NuClear.VStore.Objects;
using NuClear.VStore.Options;
using NuClear.VStore.Prometheus;
using NuClear.VStore.S3;
using NuClear.VStore.Templates;
using Prometheus;

using RedLockNet;

using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace NuClear.VStore.Renderer
{
    public sealed class Startup
    {
        private const string Aws = "AWS";
        private const string Ceph = "Ceph";

        private readonly IHostingEnvironment _environment;
        private readonly IConfiguration _configuration;

        public Startup(IHostingEnvironment environment, IConfiguration configuration)
        {
            _environment = environment;
            _configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services
                .AddOptions()
                .Configure<CephOptions>(_configuration.GetSection("Ceph"))
                .Configure<ThrottlingOptions>(_configuration.GetSection("Throttling"));

            services.AddMvcCore()
                    .SetCompatibilityVersion(CompatibilityVersion.Version_2_2)
                    .AddApiExplorer()
                    .AddCors()
                    .AddJsonFormatters();

            services.AddVersionedApiExplorer()
                    .AddApiVersioning(
                        options =>
                            {
                                options.ReportApiVersions = true;
                                options.DefaultApiVersion = new ApiVersion(1, 0);
                                options.AssumeDefaultVersionWhenUnspecified = true;
                            });

            services.AddMemoryCache();
            services.AddSwaggerGen(
                options =>
                    {
                        var provider = services.BuildServiceProvider().GetRequiredService<IApiVersionDescriptionProvider>();
                        foreach (var description in provider.ApiVersionDescriptions)
                        {
                            options.SwaggerDoc(description.GroupName, new Info { Title = $"VStore Renderer API {description.ApiVersion}", Version = description.ApiVersion.ToString() });
                        }

                        options.OperationFilter<ImplicitApiVersionParameter>();
                        options.OperationFilter<UploadFileOperationFilter>();
                        options.OperationFilter<ViewFileFilter>();
                        options.IncludeXmlComments(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{nameof(VStore)}.{nameof(Renderer)}.xml"));
                    });
        }

        public void ConfigureContainer(ContainerBuilder builder)
        {
            var awsOptions = _configuration.GetAWSOptions(Ceph);

            builder.Register(x => x.Resolve<IOptions<CephOptions>>().Value).SingleInstance();
            builder.Register(x => x.Resolve<IOptions<ThrottlingOptions>>().Value).SingleInstance();
            builder.RegisterInstance(new CdnOptions { CdnUrl = new Uri("ams://unused") }).SingleInstance();
            builder.RegisterInstance(new DistributedLockOptions()).SingleInstance();
            builder.RegisterInstance(awsOptions).SingleInstance();

            builder.RegisterType<InMemoryLockFactory>().As<IDistributedLockFactory>().PreserveExistingDefaults().SingleInstance();
            builder.Register(
                        x =>
                            {
                                var config = awsOptions.DefaultClientConfig.ToS3Config();
                                config.ForcePathStyle = true;

                                var cephOptions = x.Resolve<CephOptions>();
                                return new Amazon.S3.AmazonS3Client(new BasicAWSCredentials(cephOptions.AccessKey, cephOptions.SecretKey), config);
                            })
                    .Named<IAmazonS3>(Ceph)
                    .SingleInstance();
            builder.Register(
                        x =>
                            {
                                var options = _configuration.GetAWSOptions(Aws);
                                return options.CreateServiceClient<IAmazonS3>();
                            })
                    .Named<IAmazonS3>(Aws)
                    .SingleInstance();
            builder.RegisterType<S3MultipartUploadClient>()
                   .As<IS3MultipartUploadClient>()
                   .WithParameter(
                       (parameterInfo, context) => parameterInfo.ParameterType == typeof(IAmazonS3),
                       (parameterInfo, context) => context.ResolveNamed<IAmazonS3>(Ceph))
                   .SingleInstance();
            builder.Register(
                        x =>
                            {
                                var amazonS3 = x.ResolveNamed<IAmazonS3>(Ceph);
                                var metricsProvider = x.Resolve<MetricsProvider>();
                                return new S3ClientPrometheusDecorator(new S3Client(amazonS3), metricsProvider, Labels.Backends.Ceph);
                            })
                    .Named<IS3Client>(Ceph)
                    .PreserveExistingDefaults()
                    .SingleInstance();
            builder.Register(
                        x =>
                            {
                                var amazonS3 = x.ResolveNamed<IAmazonS3>(Aws);
                                var metricsProvider = x.Resolve<MetricsProvider>();
                                return new S3ClientPrometheusDecorator(new S3Client(amazonS3), metricsProvider, Labels.Backends.Aws);
                            })
                    .Named<IS3Client>(Aws)
                    .PreserveExistingDefaults()
                    .SingleInstance();
            builder.RegisterType<CephS3Client>()
                    .As<ICephS3Client>()
                    .WithParameter(
                        (parameterInfo, context) => parameterInfo.ParameterType == typeof(IS3Client),
                        (parameterInfo, context) => context.ResolveNamed<IS3Client>(Ceph))
                    .SingleInstance();
            builder.RegisterType<S3.AmazonS3Client>()
                    .As<IAmazonS3Client>()
                    .WithParameter(
                        (parameterInfo, context) => parameterInfo.ParameterType == typeof(IS3Client),
                        (parameterInfo, context) => context.ResolveNamed<IS3Client>(Aws))
                    .SingleInstance();
            builder.RegisterType<DistributedLockManager>().SingleInstance();
            builder.RegisterType<TemplatesStorageReader>()
                   .WithParameter(
                       (parameterInfo, context) => parameterInfo.ParameterType == typeof(IS3Client),
                       (parameterInfo, context) => context.Resolve<ICephS3Client>())
                   .As<ITemplatesStorageReader>()
                   .SingleInstance();
            builder.RegisterType<ObjectsStorageReader>()
                   .WithParameter(
                       (parameterInfo, context) => parameterInfo.ParameterType == typeof(IS3Client),
                       (parameterInfo, context) => context.Resolve<ICephS3Client>())
                   .As<IObjectsStorageReader>()
                   .SingleInstance();
            builder.RegisterType<ImagePreviewService>()
                   .WithParameter(
                       (parameterInfo, context) => parameterInfo.ParameterType == typeof(IS3Client),
                       (parameterInfo, context) => context.Resolve<ICephS3Client>())
                   .SingleInstance();
            builder.RegisterType<RawFileStorageInfoProvider>().SingleInstance();
            builder.RegisterType<MetricsProvider>().SingleInstance();

            builder.RegisterType<MemoryBasedRequestLimiter>().As<IRequestLimiter>().SingleInstance();
            SixLabors.ImageSharp.Configuration.Default.MemoryAllocator =
                _environment.IsProduction()
                    ? ArrayPoolMemoryAllocatorFactory.CreateWithLimitedLargePooling()
                    : ArrayPoolMemoryAllocatorFactory.CreateWithLimitedSmallPooling();
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseMiddleware<HealthCheckMiddleware>();
            app.UseMetricServer();
            app.UseMiddleware<CrosscuttingTraceIdentifierMiddleware>();
            if (!_environment.IsProduction())
            {
                app.UseMiddleware<LogUnsuccessfulResponseMiddleware>();
            }

            app.UseExceptionHandler(
                new ExceptionHandlerOptions
                    {
                        ExceptionHandler =
                            async context =>
                                {
                                    var feature = context.Features.Get<IExceptionHandlerFeature>();
                                    var error = new JObject
                                        {
                                            { "requestId", context.TraceIdentifier },
                                            { "code", "unhandledException" },
                                            { "message", feature.Error.Message }
                                        };

                                    if (_environment.IsDevelopment())
                                    {
                                        error.Add("details", feature.Error.ToString());
                                    }

                                    context.Response.ContentType = ContentType.Json;
                                    await context.Response.WriteAsync(new JObject(new JProperty("error", error)).ToString());
                                }
                    });
            app.UseCors(builder => builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader().WithExposedHeaders("Location"));
            app.UseMvc();

            if (!_environment.IsProduction())
            {
                app.UseSwagger();
                app.UseSwaggerUI(
                    options =>
                        {
                            var provider = app.ApplicationServices.GetRequiredService<IApiVersionDescriptionProvider>();
                            foreach (var description in provider.ApiVersionDescriptions)
                            {
                                options.SwaggerEndpoint($"/swagger/{description.GroupName}/swagger.json", description.GroupName.ToUpperInvariant());
                            }

                            options.DocExpansion(DocExpansion.None);
                            options.EnableValidator();
                            options.ShowExtensions();
                            options.DisplayRequestDuration();
                        });
            }
        }
    }
}