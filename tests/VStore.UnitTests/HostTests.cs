using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Autofac;
using Autofac.Extensions.DependencyInjection;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

using Moq;

using Newtonsoft.Json.Linq;

using NuClear.VStore.Configuration;
using NuClear.VStore.DataContract;
using NuClear.VStore.Descriptors;
using NuClear.VStore.Descriptors.Objects;
using NuClear.VStore.Descriptors.Templates;
using NuClear.VStore.Host;
using NuClear.VStore.Locks;
using NuClear.VStore.Objects;
using NuClear.VStore.Options;
using NuClear.VStore.S3;
using NuClear.VStore.Templates;

using RedLockNet;

using Xunit;
using Xunit.Abstractions;

namespace VStore.UnitTests
{
    public class HostTests : IDisposable
    {
        private readonly ITestOutputHelper _testOutputHelper;
        private readonly TestServer _server;
        private readonly HttpClient _client;
        private readonly Mock<IObjectsStorageReader> _mockObjectsStorageReader = new Mock<IObjectsStorageReader>();
        private readonly Mock<IObjectsManagementService> _mockObjectsManagementService = new Mock<IObjectsManagementService>();
        private readonly Mock<ITemplatesManagementService> _mockTemplatesManagementService = new Mock<ITemplatesManagementService>();
        private static readonly JTokenEqualityComparer JTokenEqualityComparer = new JTokenEqualityComparer();

        public HostTests(ITestOutputHelper testOutputHelper)
        {
            _testOutputHelper = testOutputHelper;
            _server = new TestServer(
                new WebHostBuilder()
                    .UseEnvironment(EnvironmentName.Development)
                    .ConfigureAppConfiguration((hostingContext, config) =>
                                                   {
                                                       var env = hostingContext.HostingEnvironment;
                                                       config.UseDefaultConfiguration(env.ContentRootPath, env.EnvironmentName);
                                                   })
                    .ConfigureServices(
                        services =>
                            {
                                services.AddAutofac(
                                    x =>
                                        {
                                            x.RegisterInstance(new InMemoryLockFactory()).As<IDistributedLockFactory>();
                                            x.RegisterInstance(_mockObjectsStorageReader.Object).As<IObjectsStorageReader>();
                                            x.RegisterInstance(_mockObjectsManagementService.Object).As<IObjectsManagementService>();
                                            x.RegisterInstance(_mockTemplatesManagementService.Object).As<ITemplatesManagementService>();
                                        });
                            })
                    .UseStartup<Startup>());

            _client = _server.CreateClient();
            _client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJpc3MiOiJva2FwaSJ9.001QCdGC5mXuecjP1OfhafA6BsBB56ASHdoKA4btkak");
        }

        [Theory]
        [InlineData("1.0")]
        [InlineData("1.1")]
        [InlineData("1.2")]
        [InlineData("0.9", false)]
        [InlineData("1.3", false)]
        [InlineData("2.0", false)]
        public async Task TestGetAvailableElementDescriptors(string version, bool shouldSuccess = true)
        {
            var unmockedService = new TemplatesManagementService(new UploadFileOptions(), new CephOptions(), null, null, null);
            _mockTemplatesManagementService.Reset();
            _mockTemplatesManagementService.Setup(x => x.GetAvailableElementDescriptors())
                                           .Returns(unmockedService.GetAvailableElementDescriptors());

            using (var response = await _client.GetAsync($"/api/{version}/templates/element-descriptors/available"))
            {
                if (shouldSuccess)
                {
                    response.EnsureSuccessStatusCode();
                    var stringResponse = await response.Content.ReadAsStringAsync();
                    var json = JArray.Parse(stringResponse);
                    Assert.NotEmpty(json);
                    Assert.Equal(Enum.GetNames(typeof(ElementDescriptorType)).Length, json.Count);
                }
                else
                {
                    Assert.False(response.IsSuccessStatusCode);
                }
            }

            _mockTemplatesManagementService.Reset();
        }

        [Fact]
        public async Task TestAmbigiousRoutes()
        {
            _mockObjectsStorageReader.Reset();
            _mockObjectsStorageReader.Setup(x => x.GetObjectDescriptor(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                                     .ThrowsAsync(new ObjectNotFoundException(string.Empty));

            const long ObjectId = 123L;
            const string ObjectVersion = "some_version_id";
            using (var response = await _client.GetAsync($"/api/1.0/objects/{ObjectId}/{ObjectVersion}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                _mockObjectsStorageReader.Verify(x => x.GetObjectDescriptor(ObjectId, ObjectVersion, It.IsAny<CancellationToken>()), Times.Exactly(1));
            }

            _mockObjectsStorageReader.Invocations.Clear();
            _mockObjectsStorageReader.Setup(x => x.GetObjectVersionsMetadata(It.IsAny<long>(), It.IsAny<string>()))
                                     .ThrowsAsync(new ObjectNotFoundException(string.Empty));

            using (var response = await _client.GetAsync($"/api/1.0/objects/{ObjectId}/versions"))
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                _mockObjectsStorageReader.Verify(x => x.GetObjectVersionsMetadata(ObjectId, It.Is<string>(p => string.IsNullOrEmpty(p))), Times.Exactly(1));
            }

            _mockObjectsStorageReader.Reset();
        }

        [Fact]
        public async Task TestObjectSerialization()
        {
            var descriptor = new ObjectDescriptor
                {
                    Id = 100500L,
                    VersionId = "QWERTYasdf_123456",
                    Language = Language.Es,
                    LastModified = DateTime.UtcNow,
                    TemplateId = 123456L,
                    TemplateVersionId = "TEMPLATE_VERSION+123)(*&^%",
                    Properties = JObject.FromObject(new { MyProp = 369, SecondProp = "best_string" }),
                    Metadata = new ObjectDescriptor.ObjectMetadata
                        {
                            Author = "id",
                            AuthorLogin = "login",
                            AuthorName = "name",
                            ModifiedElements = Array.Empty<int>()
                        },
                    Elements = new[]
                        {
                            new ObjectElementDescriptor
                                {
                                    Id = 100L,
                                    TemplateCode = 1,
                                    Type = ElementDescriptorType.FasComment,
                                    Value = new FasElementValue { Raw = "alcohol", Text = "Danger!" },
                                    Properties = JObject.FromObject(new { Name = "First element" }),
                                    Constraints = new ConstraintSet(new[]
                                        {
                                            new ConstraintSetItem(Language.Unspecified, new PlainTextElementConstraints { MaxLines = 1, MaxSymbols = 440, MaxSymbolsPerWord = null })
                                        })
                                },
                            new ObjectElementDescriptor
                                {
                                    Id = 101L,
                                    TemplateCode = 2,
                                    Type = ElementDescriptorType.FormattedText,
                                    Value = new TextElementValue { Raw = "<b>Bold</b> text" },
                                    Properties = JObject.FromObject(new { Name = "Second element", AdditionalProp = 123 }),
                                    Constraints = new ConstraintSet(new[]
                                        {
                                            new ConstraintSetItem(Language.Unspecified, new FormattedTextElementConstraints { MaxLines = 3, MaxSymbols = 80, MaxSymbolsPerWord = 10 })
                                        })
                                }
                        }
                };

            _mockObjectsStorageReader.Reset();
            _mockObjectsStorageReader.Setup(x => x.GetObjectDescriptor(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                                     .ReturnsAsync(descriptor);

            using (var response = await _client.GetAsync($"/api/1.0/objects/{descriptor.Id}/{descriptor.VersionId}"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(descriptor.VersionId, response.Headers.ETag.Tag.Trim('\"'));
                _mockObjectsStorageReader.Verify(x => x.GetObjectDescriptor(descriptor.Id, descriptor.VersionId, It.IsAny<CancellationToken>()), Times.Exactly(1));

                var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(descriptor.Id, json["id"]);
                Assert.Equal(descriptor.VersionId, json["versionId"]);
                Assert.Equal(descriptor.Language.ToString().ToLowerInvariant(), json["language"]);
                Assert.Equal(descriptor.LastModified, json["lastModified"]);
                Assert.Equal(descriptor.TemplateId, json["templateId"]);
                Assert.Equal(descriptor.TemplateVersionId, json["templateVersionId"]);
                Assert.Equal(descriptor.Properties, json["properties"], JTokenEqualityComparer);
                Assert.Null(json["modifiedElements"]);
                Assert.Equal(descriptor.Metadata.Author, json["author"]);
                Assert.Equal(descriptor.Metadata.AuthorLogin, json["authorLogin"]);
                Assert.Equal(descriptor.Metadata.AuthorName, json["authorName"]);
                Assert.Equal(descriptor.Elements.Count, json["elements"].Children().Count());

                foreach (var (expected, actual) in descriptor.Elements.Zip(json["elements"], (a, b) => (Expected: a, Actual: b)))
                {
                    Assert.Equal(expected.Id, actual["id"]);
                    Assert.Equal(expected.TemplateCode, actual["templateCode"]);
                    Assert.Equal(expected.Type.ToString(), actual["type"].ToString(), StringComparer.OrdinalIgnoreCase);
                    Assert.Equal(expected.Properties, actual["properties"], JTokenEqualityComparer);
                    Assert.Equal(((IObjectElementRawValue)expected.Value).Raw, actual["value"]["raw"]);
                }
            }

            _mockObjectsStorageReader.Reset();
        }

        [Theory]
        [InlineData("1.0")]
        public async Task TestObjectDeserialization(string apiVersion)
        {
            const long ObjectId = 123L;
            const string CreatedObjectVersion = "some_version_id";
            var authorInfo = new AuthorInfo("id", "login", "name");
            IObjectDescriptor receivedDescriptor = null;

            _mockObjectsManagementService.Reset();
            _mockObjectsManagementService.Setup(x => x.Create(It.IsAny<long>(), It.IsAny<AuthorInfo>(), It.IsAny<IObjectDescriptor>()))
                                         .Callback<long, AuthorInfo, IObjectDescriptor>((id, author, descriptor) => receivedDescriptor = descriptor)
                                         .ReturnsAsync(CreatedObjectVersion);

            const string ObjectJson =
@"{
    ""templateId"": ""200502"",
    ""templateVersionId"": ""hxRQGIv7qfw3oZJcqbspYvWK7AAXCFi"",
    ""properties"": { ""baz"": 123, ""foo"": ""bar"" },
    ""language"": ""en"",
    ""elements"": [{
        ""type"": ""plainText"",
        ""templateCode"": 911,
        ""id"": 100500,
        ""value"": { ""raw"": ""MyValue"" },
        ""properties"": { ""foo"": ""bar"", ""baz"": [ 321, 456 ] },
        ""constraints"": {
            ""unspecified"": {
                ""maxSymbols"": 10,
                ""maxSymbolsPerWord"": null,
                ""maxLines"": 2
            }
        }
    }]
}";

            using (var httpContent = new StringContent(ObjectJson, Encoding.UTF8, NuClear.VStore.Http.ContentType.Json))
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/{apiVersion}/objects/{ObjectId}"))
                {
                    request.Content = httpContent;
                    request.Headers.Add(NuClear.VStore.Http.HeaderNames.AmsAuthor, authorInfo.Author);
                    request.Headers.Add(NuClear.VStore.Http.HeaderNames.AmsAuthorLogin, authorInfo.AuthorLogin);
                    request.Headers.Add(NuClear.VStore.Http.HeaderNames.AmsAuthorName, authorInfo.AuthorName);
                    using (var response = await _client.SendAsync(request))
                    {
                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        Assert.NotNull(response.Headers.Location);
                        Assert.Equal($"/api/{apiVersion}/objects/{ObjectId}/{CreatedObjectVersion}", response.Headers.Location.PathAndQuery);
                        Assert.Equal($"\"{CreatedObjectVersion}\"", response.Headers.ETag.Tag);

                        Assert.NotNull(receivedDescriptor);
                        Assert.Equal(Language.En, receivedDescriptor.Language);
                        Assert.Equal(200502L, receivedDescriptor.TemplateId);
                        Assert.Equal("hxRQGIv7qfw3oZJcqbspYvWK7AAXCFi", receivedDescriptor.TemplateVersionId);
                        Assert.Equal(JObject.Parse(@"{""foo"": ""bar"", ""baz"": 123}"), receivedDescriptor.Properties, JTokenEqualityComparer);

                        Assert.Single(receivedDescriptor.Elements);
                        var element = receivedDescriptor.Elements.First();
                        Assert.Equal(ElementDescriptorType.PlainText, element.Type);
                        Assert.Equal(100500L, element.Id);
                        Assert.Equal(911, element.TemplateCode);
                        Assert.Equal(JObject.Parse(@"{""foo"": ""bar"", ""baz"": [ 321, 456 ]}"), element.Properties, JTokenEqualityComparer);

                        Assert.Single(element.Constraints);
                        var constraintSetItem = element.Constraints.First<ConstraintSetItem>();
                        Assert.IsType<PlainTextElementConstraints>(constraintSetItem.ElementConstraints);
                        var constraints = (PlainTextElementConstraints)constraintSetItem.ElementConstraints;
                        Assert.Equal(new PlainTextElementConstraints { MaxLines = 2, MaxSymbols = 10, MaxSymbolsPerWord = null }, constraints);

                        _mockObjectsManagementService.Verify(x => x.Create(ObjectId,
                                                                           It.Is<AuthorInfo>(a => a.Author == authorInfo.Author &&
                                                                                                  a.AuthorLogin == authorInfo.AuthorLogin &&
                                                                                                  a.AuthorName == authorInfo.AuthorName),
                                                                           It.IsAny<IObjectDescriptor>()),
                                                             Times.Exactly(1));
                    }
                }
            }

            _mockObjectsManagementService.Reset();
        }

        [Theory]
        [InlineData("1.0")]
        [InlineData("1.1")]
        [InlineData("1.2")]
        public async Task TestTemplateDeserialization(string apiVersion)
        {
            const long TemplateId = 100500L;
            const string CreatedTemplateVersion = "some_version_id";
            var authorInfo = new AuthorInfo("id", "login", "name");
            ITemplateDescriptor receivedDescriptor = null;

            _mockTemplatesManagementService.Reset();
            _mockTemplatesManagementService.Setup(x => x.CreateTemplate(It.IsAny<long>(), It.IsAny<AuthorInfo>(), It.IsAny<ITemplateDescriptor>()))
                                           .Callback<long, AuthorInfo, ITemplateDescriptor>((id, author, descriptor) => receivedDescriptor = descriptor)
                                           .ReturnsAsync(CreatedTemplateVersion);

            const string ObjectJson =
@"{
    ""properties"": { ""baz"": 123, ""foo"": ""bar"" },
    ""elements"": [{
        ""type"": ""plainText"",
        ""templateCode"": 911,
        ""id"": 100500,
        ""properties"": { ""foo"": ""bar"", ""baz"": [ 321, 456 ] },
        ""constraints"": {
            ""unspecified"": {
                ""maxSymbols"": 10,
                ""maxSymbolsPerWord"": null,
                ""maxLines"": 2
            }
        }
    }]
}";

            using (var httpContent = new StringContent(ObjectJson, Encoding.UTF8, NuClear.VStore.Http.ContentType.Json))
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/{apiVersion}/templates/{TemplateId}"))
                {
                    request.Content = httpContent;
                    request.Headers.Add(NuClear.VStore.Http.HeaderNames.AmsAuthor, authorInfo.Author);
                    request.Headers.Add(NuClear.VStore.Http.HeaderNames.AmsAuthorLogin, authorInfo.AuthorLogin);
                    request.Headers.Add(NuClear.VStore.Http.HeaderNames.AmsAuthorName, authorInfo.AuthorName);
                    using (var response = await _client.SendAsync(request))
                    {
                        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                        Assert.NotNull(response.Headers.Location);
                        Assert.Equal($"/api/{apiVersion}/templates/{TemplateId}/{CreatedTemplateVersion}", response.Headers.Location.PathAndQuery);
                        Assert.Equal($"\"{CreatedTemplateVersion}\"", response.Headers.ETag.Tag);

                        Assert.NotNull(receivedDescriptor);
                        Assert.Equal(JObject.Parse(@"{""foo"": ""bar"", ""baz"": 123}"), receivedDescriptor.Properties, JTokenEqualityComparer);

                        Assert.Single(receivedDescriptor.Elements);
                        var element = receivedDescriptor.Elements.First();
                        Assert.Equal(ElementDescriptorType.PlainText, element.Type);
                        Assert.Equal(911, element.TemplateCode);
                        Assert.Equal(JObject.Parse(@"{""foo"": ""bar"", ""baz"": [ 321, 456 ]}"), element.Properties, JTokenEqualityComparer);

                        Assert.Single(element.Constraints);
                        var constraintSetItem = element.Constraints.First<ConstraintSetItem>();
                        Assert.IsType<PlainTextElementConstraints>(constraintSetItem.ElementConstraints);
                        var constraints = (PlainTextElementConstraints)constraintSetItem.ElementConstraints;
                        Assert.Equal(new PlainTextElementConstraints { MaxLines = 2, MaxSymbols = 10, MaxSymbolsPerWord = null }, constraints);

                        _mockTemplatesManagementService.Verify(x => x.CreateTemplate(TemplateId,
                                                                                     It.Is<AuthorInfo>(a => a.Author == authorInfo.Author &&
                                                                                                            a.AuthorLogin == authorInfo.AuthorLogin &&
                                                                                                            a.AuthorName == authorInfo.AuthorName),
                                                                                     It.IsAny<ITemplateDescriptor>()),
                                                               Times.Exactly(1));
                    }
                }
            }

            _mockTemplatesManagementService.Reset();
        }

        [Theory]
        [InlineData("{ }")]
        [InlineData("{ ; }")]
        [InlineData(@"{ ""elements"": [{""id"": 1,""type"": ""badType!"", ""value"": { } }] }")]
        [InlineData(@"{ ""elements"": [{""id"": 1,""type"": ""article"", ""value"": { } }] }")]
        [InlineData(@"{ ""elements"": [{""type"": ""article"", ""id"": 1,""templateCode"": 1, ""value"": { } }] }")]
        [InlineData(@"{ ""elements"": [{""type"": ""plainText"",""templateCode"": 1,""value"": { },""constraints"": { } }] }")]
        [InlineData(@"{ ""elements"": [{""id"": 1, ""type"": ""fasComment"",""templateCode"": 1,""value"": { },""constraints"": { } }] }")]
        [InlineData(@"{ ""elements"": [{""properties"": { }, ""id"": 1, ""type"": ""fasComment"",""templateCode"": 1,""value"": { },""constraints"": { } }] }")]
        [InlineData(@"{ ""elements"": [{""id"": 1,""type"": ""plainText"",""templateCode"": 1,""properties"": { } }] }")]
        public async Task TestIncorrectObjectDeserialization(string json)
        {
            _mockObjectsManagementService.Reset();
            using (var httpContent = new StringContent(json, Encoding.UTF8, NuClear.VStore.Http.ContentType.Json))
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/1.0/objects/100500"))
                {
                    request.Content = httpContent;
                    request.Headers.Add(NuClear.VStore.Http.HeaderNames.AmsAuthor, "id");
                    request.Headers.Add(NuClear.VStore.Http.HeaderNames.AmsAuthorLogin, "login");
                    request.Headers.Add(NuClear.VStore.Http.HeaderNames.AmsAuthorName, "name");
                    using (var response = await _client.SendAsync(request))
                    {
                        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                        _mockObjectsManagementService.Verify(x => x.Create(It.IsAny<long>(), It.IsAny<AuthorInfo>(), It.IsAny<IObjectDescriptor>()), Times.Never());
                        _testOutputHelper.WriteLine(await response.Content.ReadAsStringAsync());
                    }
                }
            }

            _mockObjectsManagementService.Reset();
        }

        public void Dispose()
        {
            _server?.Dispose();
            _client?.Dispose();
        }
    }
}
