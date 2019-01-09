using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Amazon.Runtime;
using Amazon.S3;

using Autofac;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using NuClear.VStore.Host.Options;
using NuClear.VStore.Http;
using NuClear.VStore.Http.Core.Json;
using NuClear.VStore.Http.Core.Middleware;
using NuClear.VStore.Http.Core.Routing;
using NuClear.VStore.Http.Core.Swashbuckle;
using NuClear.VStore.Json;
using NuClear.VStore.Kafka;
using NuClear.VStore.Locks;
using NuClear.VStore.Objects;
using NuClear.VStore.Options;
using NuClear.VStore.Prometheus;
using NuClear.VStore.S3;
using NuClear.VStore.Sessions;
using NuClear.VStore.Templates;
using Prometheus;

using RedLockNet;

using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace NuClear.VStore.Host
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class Startup
    {
        private const string Aws = "AWS";
        private const string Ceph = "Ceph";

        private static readonly JsonConverter[] CustomConverters =
            {
                new StringEnumConverter { CamelCaseText = true },
                new Int64ToStringJsonConverter(),
                new ElementDescriptorJsonConverter(),
                new ElementDescriptorCollectionJsonConverter(),
                new TemplateDescriptorJsonConverter(),
                new ObjectElementDescriptorJsonConverter(),
                new ObjectDescriptorJsonConverter()
            };

        private readonly IConfiguration _configuration;

        public Startup(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            var jwtConfiguration = _configuration.GetSection("Jwt");
            services
                 .AddOptions()
                 .Configure<CephOptions>(_configuration.GetSection("Ceph"))
                 .Configure<DistributedLockOptions>(_configuration.GetSection("DistributedLocks"))
                 .Configure<UploadFileOptions>(_configuration.GetSection("VStore"))
                 .Configure<SessionOptions>(_configuration.GetSection("VStore"))
                 .Configure<CdnOptions>(_configuration.GetSection("VStore"))
                 .Configure<JwtOptions>(jwtConfiguration)
                 .Configure<KafkaOptions>(_configuration.GetSection("Kafka"))
                 .Configure<RouteOptions>(options => options.ConstraintMap.Add("lang", typeof(LanguageRouteConstraint)));

            services.AddMvcCore(
                        options =>
                            {
                                var policy = new AuthorizationPolicyBuilder()
                                             .RequireAuthenticatedUser()
                                             .Build();
                                options.Filters.Add(new AuthorizeFilter(policy));
                            })
                    .SetCompatibilityVersion(CompatibilityVersion.Version_2_2)
                    .AddApiExplorer()
                    .AddAuthorization()
                    .AddCors()
                    .AddJsonFormatters()
                    .AddJsonOptions(
                        options =>
                            {
                                var settings = options.SerializerSettings;

                                settings.Culture = CultureInfo.InvariantCulture;
                                settings.ContractResolver = new CamelCasePropertyNamesContractResolver();
                                for (var index = 0; index < CustomConverters.Length; ++index)
                                {
                                    settings.Converters.Insert(index, CustomConverters[index]);
                                }
                            });

            services.AddVersionedApiExplorer();

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                    .AddJwtBearer(options =>
                                      {
                                          var jwtOptions = jwtConfiguration.Get<JwtOptions>();
                                          options.TokenValidationParameters = new TokenValidationParameters
                                              {
                                                  ValidateIssuer = true,
                                                  ValidIssuer = jwtOptions.Issuer,

                                                  ValidateAudience = false,

                                                  ValidateIssuerSigningKey = true,
                                                  IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(jwtOptions.SecretKey)),

                                                  ValidateLifetime = false,
                                                  LifetimeValidator = (notBefore, expires, securityToken, validationParameters) =>
                                                                          {
                                                                              var utcNow = DateTime.UtcNow;
                                                                              return !(notBefore > utcNow || utcNow > expires);
                                                                          }
                                              };
                                      });

            services.AddApiVersioning(options => options.ReportApiVersions = true);
            services.AddMemoryCache();

            services.AddSwaggerGen(
                options =>
                    {
                        var provider = services.BuildServiceProvider().GetRequiredService<IApiVersionDescriptionProvider>();
                        foreach (var description in provider.ApiVersionDescriptions)
                        {
                            options.SwaggerDoc(description.GroupName, new Info { Title = $"VStore API {description.ApiVersion}", Version = description.ApiVersion.ToString() });
                        }

                        options.AddSecurityDefinition(
                            "Bearer",
                            new ApiKeyScheme
                                {
                                    Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
                                    Name = "Authorization",
                                    In = "header",
                                    Type = "apiKey"
                                });

                        options.AddSecurityRequirement(new Dictionary<string, IEnumerable<string>>
                            {
                                { "Bearer", new string[] { } }
                            });

                        options.OperationFilter<ImplicitApiVersionParameter>();
                        options.OperationFilter<UploadFileOperationFilter>();
                        options.IncludeXmlComments(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{nameof(VStore)}.{nameof(Host)}.xml"));
                    });
        }

        public void ConfigureContainer(ContainerBuilder builder)
        {
            builder.Register(x => x.Resolve<IOptions<CephOptions>>().Value).SingleInstance();
            builder.Register(x => x.Resolve<IOptions<DistributedLockOptions>>().Value).SingleInstance();
            builder.Register(x => x.Resolve<IOptions<UploadFileOptions>>().Value).SingleInstance();
            builder.Register(x => x.Resolve<IOptions<SessionOptions>>().Value).SingleInstance();
            builder.Register(x => x.Resolve<IOptions<CdnOptions>>().Value).SingleInstance();
            builder.Register(x => x.Resolve<IOptions<JwtOptions>>().Value).SingleInstance();
            builder.Register(x => x.Resolve<IOptions<KafkaOptions>>().Value).SingleInstance();

            builder.RegisterType<ReliableRedLockFactory>().SingleInstance();
            builder.Register<IDistributedLockFactory>(
                       x =>
                           {
                               var lockOptions = x.Resolve<DistributedLockOptions>();
                               if (lockOptions.DeveloperMode)
                               {
                                   return new InMemoryLockFactory();
                               }

                               return x.Resolve<ReliableRedLockFactory>();
                           })
                   .As<IDistributedLockFactory>()
                   .PreserveExistingDefaults()
                   .SingleInstance();
            builder.Register(
                        x =>
                            {
                                var awsOptions = _configuration.GetAWSOptions(Ceph);
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
            builder.RegisterType<SessionStorageReader>().SingleInstance();
            builder.RegisterType<SessionManagementService>().SingleInstance();
            builder.RegisterType<TemplatesStorageReader>()
                   .WithParameter(
                       (parameterInfo, context) => parameterInfo.ParameterType == typeof(IS3Client),
                       (parameterInfo, context) => context.Resolve<ICephS3Client>())
                   .As<ITemplatesStorageReader>()
                   .SingleInstance();
            builder.RegisterType<TemplatesManagementService>()
                   .WithParameter(
                       (parameterInfo, context) => parameterInfo.ParameterType == typeof(IS3Client),
                       (parameterInfo, context) => context.Resolve<ICephS3Client>())
                   .SingleInstance();
            builder.RegisterType<ObjectsStorageReader>()
                   .WithParameter(
                       (parameterInfo, context) => parameterInfo.ParameterType == typeof(IS3Client),
                       (parameterInfo, context) => context.Resolve<ICephS3Client>())
                   .As<IObjectsStorageReader>()
                   .PreserveExistingDefaults()
                   .SingleInstance();
            builder.RegisterType<ObjectsManagementService>()
                   .WithParameter(
                       (parameterInfo, context) => parameterInfo.ParameterType == typeof(IS3Client),
                       (parameterInfo, context) => context.Resolve<ICephS3Client>())
                   .As<IObjectsManagementService>()
                   .PreserveExistingDefaults()
                   .SingleInstance();
            builder.RegisterType<EventSender>().As<IEventSender>().SingleInstance();
            builder.RegisterType<MetricsProvider>().SingleInstance();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            app.UseMiddleware<HealthCheckMiddleware>();
            app.UseMetricServer();
            app.UseMiddleware<CrosscuttingTraceIdentifierMiddleware>();

            if (!env.IsProduction())
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

                                    if (env.IsDevelopment())
                                    {
                                        error.Add("details", feature.Error.ToString());
                                    }

                                    context.Response.ContentType = ContentType.Json;
                                    await context.Response.WriteAsync(new JObject(new JProperty("error", error)).ToString());
                                }
                    });
            app.UseCors(builder => builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader().WithExposedHeaders("Location"));
            app.UseAuthentication();
            app.UseMvc();

            if (!env.IsProduction())
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
