using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Amazon.S3.Model;

using Microsoft.Extensions.Caching.Memory;

using Moq;

using Newtonsoft.Json.Linq;

using NuClear.VStore.DataContract;
using NuClear.VStore.Descriptors;
using NuClear.VStore.Descriptors.Objects;
using NuClear.VStore.Descriptors.Templates;
using NuClear.VStore.Json;
using NuClear.VStore.Kafka;
using NuClear.VStore.Locks;
using NuClear.VStore.Objects;
using NuClear.VStore.Options;
using NuClear.VStore.Prometheus;
using NuClear.VStore.S3;
using NuClear.VStore.Sessions;
using NuClear.VStore.Templates;

using Xunit;

namespace VStore.UnitTests.Persistence
{
    public sealed class ObjectPersistenceTests
    {
        private const long ObjectId = 1;
        private const string ObjectVersionId = "aQocKTNj8wKtcOWvJkTp3gaxiYbV5a1";

        private const long TemplateId = 10;
        private const string TemplateVersionId = "NnjA7kAls4AR0eGF4RAVs5zLoAs2qla";

        private static readonly AuthorInfo AuthorInfo = new AuthorInfo("test", "test", "test");

        private readonly IMemoryCache _memoryCache = Mock.Of<IMemoryCache>();
        private readonly IEventSender _eventSender = Mock.Of<IEventSender>();
        private readonly Mock<IS3Client> _s3ClientMock = new Mock<IS3Client>();
        private readonly Mock<ICephS3Client> _cephS3ClientMock = new Mock<ICephS3Client>();
        private readonly Mock<ITemplatesStorageReader> _templatesStorageReaderMock = new Mock<ITemplatesStorageReader>();
        private readonly Mock<IObjectsStorageReader> _objectsStorageReaderMock = new Mock<IObjectsStorageReader>();

        private readonly ObjectsManagementService _objectsManagementService;

        public ObjectPersistenceTests()
        {
            var cephOptions = new CephOptions();
            var distributedLockManager = new DistributedLockManager(
                new InMemoryLockFactory(),
                new DistributedLockOptions { Expiration = TimeSpan.FromHours(1) });
            var sessionStorageReader = new SessionStorageReader(cephOptions, _cephS3ClientMock.Object, _memoryCache);

            _objectsManagementService = new ObjectsManagementService(
                cephOptions,
                new KafkaOptions(),
                _s3ClientMock.Object,
                _templatesStorageReaderMock.Object,
                _objectsStorageReaderMock.Object,
                sessionStorageReader,
                distributedLockManager,
                _eventSender,
                new MetricsProvider());
        }

        [Fact]
        public async Task LanguageShouldBeSet()
        {
            var objectDescriptor = new ObjectDescriptor();

            await Assert.ThrowsAsync<ArgumentException>(
                nameof(objectDescriptor.Language),
                async () => await _objectsManagementService.Create(ObjectId, AuthorInfo, objectDescriptor));
        }

        [Fact]
        public async Task TemplateIdShouldBeSet()
        {
            var objectDescriptor = new ObjectDescriptor
                {
                    Language = Language.Ru
                };

            await Assert.ThrowsAsync<ArgumentException>(
                nameof(objectDescriptor.TemplateId),
                async () => await _objectsManagementService.Create(ObjectId, AuthorInfo, objectDescriptor));
        }

        [Fact]
        public async Task TemplateVersionIdShouldBeSet()
        {
            var objectDescriptor = new ObjectDescriptor
                {
                    Language = Language.Ru,
                    TemplateId = TemplateId
                };

            await Assert.ThrowsAsync<ArgumentException>(
                nameof(objectDescriptor.TemplateVersionId),
                async () => await _objectsManagementService.Create(ObjectId, AuthorInfo, objectDescriptor));
        }

        [Fact]
        public async Task PropertiesShouldBeSet()
        {
            var objectDescriptor = new ObjectDescriptor
                {
                    Language = Language.Ru,
                    TemplateId = TemplateId,
                    TemplateVersionId = TemplateVersionId,
                };

            await Assert.ThrowsAsync<ArgumentException>(
                nameof(objectDescriptor.Properties),
                async () => await _objectsManagementService.Create(ObjectId, AuthorInfo, objectDescriptor));
        }

        [Fact]
        public async Task ObjectShouldNotExistWhileCreation()
        {
            _objectsStorageReaderMock.Setup(m => m.IsObjectExists(It.IsAny<long>()))
                                     .Returns(() => Task.FromResult(true));
            var objectDescriptor = new ObjectDescriptor
                {
                    Language = Language.Ru,
                    TemplateId = TemplateId,
                    TemplateVersionId = TemplateVersionId,
                    Properties = new JObject()
                };

            await Assert.ThrowsAsync<ObjectAlreadyExistsException>(
                async () => await _objectsManagementService.Create(ObjectId, AuthorInfo, objectDescriptor));
        }

        [Fact]
        public async Task QuantityOfTemplateElementsAndObjectElementsShouldMatch()
        {
            const int TemplateCode = 100;

            var templateDescriptor = new TemplateDescriptor
                {
                    Id = TemplateId,
                    VersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ElementDescriptor(
                                ElementDescriptorType.PlainText,
                                TemplateCode,
                                new JObject(),
                                new ConstraintSet(Array.Empty<ConstraintSetItem>()))
                        }
                };

            _objectsStorageReaderMock.Setup(m => m.IsObjectExists(It.IsAny<long>()))
                                     .ReturnsAsync(() => false);
            _templatesStorageReaderMock.Setup(m => m.GetTemplateDescriptor(It.IsAny<long>(), It.IsAny<string>()))
                                       .ReturnsAsync(() => templateDescriptor);

            var objectDescriptor = new ObjectDescriptor
                {
                    Language = Language.Ru,
                    TemplateId = TemplateId,
                    TemplateVersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = Array.Empty<IObjectElementDescriptor>()
                };

            await Assert.ThrowsAsync<ObjectInconsistentException>(
                async () => await _objectsManagementService.Create(ObjectId, AuthorInfo, objectDescriptor));
        }

        [Fact]
        public async Task TemplateCodesInTemplateElementAndObjectElementShouldMatch()
        {
            const int TemplateCode = 100;

            var templateDescriptor = new TemplateDescriptor
                {
                    Id = TemplateId,
                    VersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ElementDescriptor(
                                ElementDescriptorType.PlainText,
                                TemplateCode,
                                new JObject(),
                                new ConstraintSet(Array.Empty<ConstraintSetItem>()))
                        }
                };

            _objectsStorageReaderMock.Setup(m => m.IsObjectExists(It.IsAny<long>()))
                                     .ReturnsAsync(() => false);
            _templatesStorageReaderMock.Setup(m => m.GetTemplateDescriptor(It.IsAny<long>(), It.IsAny<string>()))
                                       .ReturnsAsync(() => templateDescriptor);

            var objectDescriptor = new ObjectDescriptor
                {
                    Language = Language.Ru,
                    TemplateId = TemplateId,
                    TemplateVersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ObjectElementDescriptor { Type = ElementDescriptorType.PlainText }
                        }
                };

            await Assert.ThrowsAsync<ObjectInconsistentException>(
                async () => await _objectsManagementService.Create(ObjectId, AuthorInfo, objectDescriptor));
        }

        [Fact]
        public async Task ContraintsForTemplateElementAndObjectElementShouldMatch()
        {
            const int TemplateCode = 100;
            const Language Language = Language.Ru;
            var plainTextConstraints = new PlainTextElementConstraints { MaxSymbols = 100 };

            var templateDescriptor = new TemplateDescriptor
                {
                    Id = TemplateId,
                    VersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ElementDescriptor(
                                ElementDescriptorType.PlainText,
                                TemplateCode,
                                new JObject(),
                                new ConstraintSet(new[]
                                    {
                                        new ConstraintSetItem(Language, plainTextConstraints)
                                    }))
                        }
                };

            _objectsStorageReaderMock.Setup(m => m.IsObjectExists(It.IsAny<long>()))
                                     .ReturnsAsync(() => false);
            _templatesStorageReaderMock.Setup(m => m.GetTemplateDescriptor(It.IsAny<long>(), It.IsAny<string>()))
                                       .ReturnsAsync(() => templateDescriptor);

            var objectDescriptor = new ObjectDescriptor
                {
                    Language = Language,
                    TemplateId = TemplateId,
                    TemplateVersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ObjectElementDescriptor
                                {
                                    Type = ElementDescriptorType.PlainText,
                                    TemplateCode = TemplateCode
                                }
                        }
                };

            await Assert.ThrowsAsync<ObjectInconsistentException>(
                async () => await _objectsManagementService.Create(ObjectId, AuthorInfo, objectDescriptor));
        }

        [Fact]
        public async Task ObjectElementValueShouldBeSet()
        {
            const int TemplateCode = 100;
            const Language Language = Language.Ru;
            var plainTextConstraints = new PlainTextElementConstraints { MaxSymbols = 100 };

            var templateDescriptor = new TemplateDescriptor
                {
                    Id = TemplateId,
                    VersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ElementDescriptor(
                                ElementDescriptorType.PlainText,
                                TemplateCode,
                                new JObject(),
                                new ConstraintSet(new[]
                                    {
                                        new ConstraintSetItem(Language, plainTextConstraints)
                                    }))
                        }
                };

            _objectsStorageReaderMock.Setup(m => m.IsObjectExists(It.IsAny<long>()))
                                     .ReturnsAsync(() => false);
            _templatesStorageReaderMock.Setup(m => m.GetTemplateDescriptor(It.IsAny<long>(), It.IsAny<string>()))
                                       .ReturnsAsync(() => templateDescriptor);

            var objectDescriptor = new ObjectDescriptor
                {
                    Language = Language,
                    TemplateId = TemplateId,
                    TemplateVersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ObjectElementDescriptor
                                {
                                    Type = ElementDescriptorType.PlainText,
                                    TemplateCode = TemplateCode,
                                    Constraints = new ConstraintSet(new[]
                                        {
                                            new ConstraintSetItem(Language, plainTextConstraints)
                                        })
                                }
                        }
                };

            await Assert.ThrowsAsync<NullReferenceException>(
                async () => await _objectsManagementService.Create(ObjectId, AuthorInfo, objectDescriptor));
        }

        [Fact]
        public async Task S3PutObjectShouldBeCalledWhileCreation()
        {
            const int TemplateCode = 100;
            const Language Language = Language.Ru;
            var plainTextConstraints = new PlainTextElementConstraints { MaxSymbols = 100 };

            var templateDescriptor = new TemplateDescriptor
                {
                    Id = TemplateId,
                    VersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ElementDescriptor(
                                ElementDescriptorType.PlainText,
                                TemplateCode,
                                new JObject(),
                                new ConstraintSet(new[]
                                    {
                                        new ConstraintSetItem(Language, plainTextConstraints)
                                    }))
                        }
                };

            _objectsStorageReaderMock.Setup(m => m.IsObjectExists(It.IsAny<long>()))
                                     .ReturnsAsync(() => false);
            _templatesStorageReaderMock.Setup(m => m.GetTemplateDescriptor(It.IsAny<long>(), It.IsAny<string>()))
                                       .ReturnsAsync(() => templateDescriptor);
            _objectsStorageReaderMock.Setup(m => m.GetObjectLatestVersion(It.IsAny<long>()))
                                     .ReturnsAsync(
                                         new VersionedObjectDescriptor<string>(
                                             ObjectId.AsS3ObjectKey(Tokens.ObjectPostfix),
                                             ObjectVersionId,
                                             DateTime.UtcNow)
                                     );

            var objectDescriptor = new ObjectDescriptor
                {
                    Language = Language,
                    TemplateId = TemplateId,
                    TemplateVersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ObjectElementDescriptor
                                {
                                    Type = ElementDescriptorType.PlainText,
                                    TemplateCode = TemplateCode,
                                    Constraints = new ConstraintSet(new[]
                                        {
                                            new ConstraintSetItem(Language, plainTextConstraints)
                                        }),
                                    Value = new TextElementValue { Raw = "Text" }
                                }
                        }
                };

            await _objectsManagementService.Create(ObjectId, AuthorInfo, objectDescriptor);

            _s3ClientMock.Verify(m => m.PutObjectAsync(It.IsAny<PutObjectRequest>()), Times.Exactly(2));
        }

        [Fact]
        public async Task CorrectJsonShouldBePassedToS3PutObjectForTextElement()
        {
            const int TemplateCode = 100;
            const Language Language = Language.Ru;
            var plainTextConstraints = new PlainTextElementConstraints { MaxSymbols = 100 };

            var templateDescriptor = new TemplateDescriptor
                {
                    Id = TemplateId,
                    VersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ElementDescriptor(
                                ElementDescriptorType.PlainText,
                                TemplateCode,
                                new JObject(),
                                new ConstraintSet(new[]
                                    {
                                        new ConstraintSetItem(Language, plainTextConstraints)
                                    }))
                        }
                };

            _objectsStorageReaderMock.Setup(m => m.IsObjectExists(It.IsAny<long>()))
                                     .ReturnsAsync(() => false);
            _templatesStorageReaderMock.Setup(m => m.GetTemplateDescriptor(It.IsAny<long>(), It.IsAny<string>()))
                                       .ReturnsAsync(() => templateDescriptor);
            _objectsStorageReaderMock.Setup(m => m.GetObjectLatestVersion(It.IsAny<long>()))
                                     .ReturnsAsync(
                                         new VersionedObjectDescriptor<string>(
                                             ObjectId.AsS3ObjectKey(Tokens.ObjectPostfix),
                                             ObjectVersionId,
                                             DateTime.UtcNow)
                                     );

            var requests = new List<PutObjectRequest>();
            _s3ClientMock.Setup(m => m.PutObjectAsync(It.IsAny<PutObjectRequest>()))
                         .Callback<PutObjectRequest>(request => requests.Add(request))
                         .ReturnsAsync(new PutObjectResponse());

            var objectDescriptor = new ObjectDescriptor
                {
                    Language = Language,
                    TemplateId = TemplateId,
                    TemplateVersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ObjectElementDescriptor
                                {
                                    Type = ElementDescriptorType.PlainText,
                                    TemplateCode = TemplateCode,
                                    Constraints = new ConstraintSet(new[]
                                        {
                                            new ConstraintSetItem(Language, plainTextConstraints)
                                        }),
                                    Value = new TextElementValue { Raw = "Text" }
                                }
                        }
                };

            await _objectsManagementService.Create(ObjectId, AuthorInfo, objectDescriptor);

            var elementContent = requests[0].ContentBody;
            var elementJson = JObject.Parse(elementContent);
            Assert.Equal("Text", elementJson["value"]["raw"]);
        }

        [Fact]
        public async Task CorrectJsonShouldBePassedToS3PutObjectForCompositeBitmapImageElement()
        {
            const int TemplateCode = 100;
            const Language Language = Language.Ru;
            var constraints = new CompositeBitmapImageElementConstraints();

            var templateDescriptor = new TemplateDescriptor
                {
                    Id = TemplateId,
                    VersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ElementDescriptor(
                                ElementDescriptorType.CompositeBitmapImage,
                                TemplateCode,
                                new JObject(),
                                new ConstraintSet(new[]
                                    {
                                        new ConstraintSetItem(Language, constraints)
                                    }))
                        }
                };

            _objectsStorageReaderMock.Setup(m => m.IsObjectExists(It.IsAny<long>()))
                                     .ReturnsAsync(() => false);
            _templatesStorageReaderMock.Setup(m => m.GetTemplateDescriptor(It.IsAny<long>(), It.IsAny<string>()))
                                       .ReturnsAsync(() => templateDescriptor);
            _objectsStorageReaderMock.Setup(m => m.GetObjectLatestVersion(It.IsAny<long>()))
                                     .ReturnsAsync(
                                         new VersionedObjectDescriptor<string>(
                                             ObjectId.AsS3ObjectKey(Tokens.ObjectPostfix),
                                             ObjectVersionId,
                                             DateTime.UtcNow)
                                     );

            var response = new GetObjectMetadataResponse();
            var metadataWrapper = MetadataCollectionWrapper.For(response.Metadata);
            metadataWrapper.Write(MetadataElement.ExpiresAt, DateTime.UtcNow.AddDays(1));

            _cephS3ClientMock.Setup(m => m.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>()))
                             .ReturnsAsync(() => response);

            var requests = new List<PutObjectRequest>();
            _s3ClientMock.Setup(m => m.PutObjectAsync(It.IsAny<PutObjectRequest>()))
                         .Callback<PutObjectRequest>(request => requests.Add(request))
                         .ReturnsAsync(new PutObjectResponse());

            var fileKey = Guid.NewGuid().AsS3ObjectKey("key.raw");
            var objectDescriptor = new ObjectDescriptor
                {
                    Language = Language,
                    TemplateId = TemplateId,
                    TemplateVersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ObjectElementDescriptor
                                {
                                    Type = ElementDescriptorType.CompositeBitmapImage,
                                    TemplateCode = TemplateCode,
                                    Constraints = new ConstraintSet(new[]
                                        {
                                            new ConstraintSetItem(Language, constraints)
                                        }),
                                    Value = new CompositeBitmapImageElementValue
                                        {
                                            Raw = fileKey,
                                            CropArea = new CropArea(),
                                            SizeSpecificImages = Array.Empty<SizeSpecificImage>()
                                        }
                                }
                        }
                };

            await _objectsManagementService.Create(ObjectId, AuthorInfo, objectDescriptor);

            var elementContent = requests[0].ContentBody;
            var elementJson = JObject.Parse(elementContent);
            var valueJson = elementJson["value"];

            Assert.Equal(fileKey, valueJson["raw"]);
            Assert.NotNull(valueJson["filename"]);
            Assert.NotNull(valueJson["filesize"]);
            Assert.NotNull(valueJson["cropArea"]);
            Assert.NotNull(valueJson["sizeSpecificImages"]);
        }

        [Fact]
        public async Task CorrectJsonShouldBePassedToS3PutObjectForScalableBitmapImageElement()
        {
            const int TemplateCode = 100;
            const Language Language = Language.Ru;
            const Anchor Anchor = Anchor.Top;
            var constraints = new ScalableBitmapImageElementConstraints();

            var templateDescriptor = new TemplateDescriptor
                {
                    Id = TemplateId,
                    VersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ElementDescriptor(
                                ElementDescriptorType.ScalableBitmapImage,
                                TemplateCode,
                                new JObject(),
                                new ConstraintSet(new[]
                                    {
                                        new ConstraintSetItem(Language, constraints)
                                    }))
                        }
                };

            _objectsStorageReaderMock.Setup(m => m.IsObjectExists(It.IsAny<long>()))
                                     .ReturnsAsync(() => false);
            _templatesStorageReaderMock.Setup(m => m.GetTemplateDescriptor(It.IsAny<long>(), It.IsAny<string>()))
                                       .ReturnsAsync(() => templateDescriptor);
            _objectsStorageReaderMock.Setup(m => m.GetObjectLatestVersion(It.IsAny<long>()))
                                     .ReturnsAsync(
                                         new VersionedObjectDescriptor<string>(
                                             ObjectId.AsS3ObjectKey(Tokens.ObjectPostfix),
                                             ObjectVersionId,
                                             DateTime.UtcNow)
                                     );

            var response = new GetObjectMetadataResponse();
            var metadataWrapper = MetadataCollectionWrapper.For(response.Metadata);
            metadataWrapper.Write(MetadataElement.ExpiresAt, DateTime.UtcNow.AddDays(1));

            _cephS3ClientMock.Setup(m => m.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>()))
                             .ReturnsAsync(() => response);

            var requests = new List<PutObjectRequest>();
            _s3ClientMock.Setup(m => m.PutObjectAsync(It.IsAny<PutObjectRequest>()))
                         .Callback<PutObjectRequest>(request => requests.Add(request))
                         .ReturnsAsync(new PutObjectResponse());

            var fileKey = Guid.NewGuid().AsS3ObjectKey("key.raw");
            var objectDescriptor = new ObjectDescriptor
                {
                    Language = Language,
                    TemplateId = TemplateId,
                    TemplateVersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ObjectElementDescriptor
                                {
                                    Type = ElementDescriptorType.ScalableBitmapImage,
                                    TemplateCode = TemplateCode,
                                    Constraints = new ConstraintSet(new[]
                                        {
                                            new ConstraintSetItem(Language, constraints)
                                        }),
                                    Value = new ScalableBitmapImageElementValue
                                        {
                                            Raw = fileKey,
                                            Anchor = Anchor
                                        }
                                }
                        }
                };

            await _objectsManagementService.Create(ObjectId, AuthorInfo, objectDescriptor);

            var elementContent = requests[0].ContentBody;
            var elementJson = JObject.Parse(elementContent);
            var valueJson = elementJson["value"];

            Assert.Equal(valueJson["raw"], fileKey);
            Assert.NotNull(valueJson["filename"]);
            Assert.NotNull(valueJson["filesize"]);
            Assert.Equal(valueJson["anchor"], Anchor.ToString().ToLower());
        }

        [Fact]
        public async Task CorrectJsonShouldBePassedToS3PutObjectForScalableBitmapImageElementWithoutAnchor()
        {
            const int TemplateCode = 100;
            const Language Language = Language.Ru;
            var constraints = new ScalableBitmapImageElementConstraints();

            var templateDescriptor = new TemplateDescriptor
                {
                    Id = TemplateId,
                    VersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ElementDescriptor(
                                ElementDescriptorType.ScalableBitmapImage,
                                TemplateCode,
                                new JObject(),
                                new ConstraintSet(new[]
                                    {
                                        new ConstraintSetItem(Language, constraints)
                                    }))
                        }
                };

            _objectsStorageReaderMock.Setup(m => m.IsObjectExists(ObjectId))
                                     .ReturnsAsync(() => false);
            _templatesStorageReaderMock.Setup(m => m.GetTemplateDescriptor(TemplateId, TemplateVersionId))
                                       .ReturnsAsync(() => templateDescriptor);
            _objectsStorageReaderMock.Setup(m => m.GetObjectLatestVersion(It.IsAny<long>()))
                                     .ReturnsAsync(
                                         new VersionedObjectDescriptor<string>(
                                             ObjectId.AsS3ObjectKey(Tokens.ObjectPostfix),
                                             ObjectVersionId,
                                             DateTime.UtcNow)
                                     );

            var response = new GetObjectMetadataResponse();
            var metadataWrapper = MetadataCollectionWrapper.For(response.Metadata);
            metadataWrapper.Write(MetadataElement.ExpiresAt, DateTime.UtcNow.AddDays(1));

            _cephS3ClientMock.Setup(m => m.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>()))
                             .ReturnsAsync(() => response);

            var requests = new List<PutObjectRequest>();
            _s3ClientMock.Setup(m => m.PutObjectAsync(It.IsAny<PutObjectRequest>()))
                         .Callback<PutObjectRequest>(request => requests.Add(request))
                         .ReturnsAsync(new PutObjectResponse());

            var fileKey = Guid.NewGuid().AsS3ObjectKey("key.raw");
            var objectDescriptor = new ObjectDescriptor
                {
                    Language = Language,
                    TemplateId = TemplateId,
                    TemplateVersionId = TemplateVersionId,
                    Properties = new JObject(),
                    Elements = new[]
                        {
                            new ObjectElementDescriptor
                                {
                                    Type = ElementDescriptorType.ScalableBitmapImage,
                                    TemplateCode = TemplateCode,
                                    Constraints = new ConstraintSet(new[]
                                        {
                                            new ConstraintSetItem(Language, constraints)
                                        }),
                                    Value = new ScalableBitmapImageElementValue
                                        {
                                            Raw = fileKey
                                        }
                                }
                        }
                };

            await _objectsManagementService.Create(ObjectId, AuthorInfo, objectDescriptor);

            var elementContent = requests[0].ContentBody;
            var elementJson = JObject.Parse(elementContent);
            var valueJson = elementJson["value"];

            Assert.Equal(fileKey, valueJson["raw"]);
            Assert.NotNull(valueJson["filename"]);
            Assert.NotNull(valueJson["filesize"]);
            Assert.Equal(nameof(Anchor.Middle).ToLower(), valueJson["anchor"]);
        }
    }
}