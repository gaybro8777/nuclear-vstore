﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

using Amazon.S3;
using Amazon.S3.Model;

using Newtonsoft.Json;

using NuClear.VStore.Descriptors;
using NuClear.VStore.Descriptors.Objects;
using NuClear.VStore.Descriptors.Templates;
using NuClear.VStore.Json;
using NuClear.VStore.Options;
using NuClear.VStore.S3;
using NuClear.VStore.Templates;

using S3ObjectVersion = NuClear.VStore.S3.S3ObjectVersion;

namespace NuClear.VStore.Objects
{
    public sealed class ObjectsStorageReader
    {
        private readonly IAmazonS3 _amazonS3;
        private readonly TemplatesStorageReader _templatesStorageReader;
        private readonly string _bucketName;

        public ObjectsStorageReader(
            CephOptions cephOptions,
            IAmazonS3 amazonS3,
            TemplatesStorageReader templatesStorageReader)
        {
            _amazonS3 = amazonS3;
            _templatesStorageReader = templatesStorageReader;
            _bucketName = cephOptions.ObjectsBucketName;
        }

        public async Task<IVersionedTemplateDescriptor> GetTemplateDescriptor(long id, string versionId)
        {
            ObjectPersistenceDescriptor persistenceDescriptor = await GetObjectFromS3<ObjectPersistenceDescriptor>(id.AsS3ObjectKey(Tokens.ObjectPostfix), versionId);
            return await _templatesStorageReader.GetTemplateDescriptor(persistenceDescriptor.TemplateId, persistenceDescriptor.TemplateVersionId);
        }

        public async Task<IReadOnlyCollection<IdentifyableObjectDescriptor>> GetAllObjectRootVersions(long id)
        {
            var versions = Array.Empty<IdentifyableObjectDescriptor>();
            ListVersionsResponse versionsResponse = null;
            do
            {
                versionsResponse = await _amazonS3.ListVersionsAsync(
                                       new ListVersionsRequest
                                           {
                                               BucketName = _bucketName,
                                               Prefix = id.AsS3ObjectKey(Tokens.ObjectPostfix),
                                               VersionIdMarker = versionsResponse?.NextVersionIdMarker
                                           });
                versions = versions.Concat(versionsResponse.Versions.Select(x => new IdentifyableObjectDescriptor(x.Key.AsRootObjectId(), x.VersionId, x.LastModified)))
                                   .ToArray();
            }
            while (versionsResponse.IsTruncated);
            return versions;
        }

        public async Task<IReadOnlyCollection<S3ObjectVersion>> GetObjectLatestVersions(long id)
        {
            var versionsResponse = await _amazonS3.ListVersionsAsync(_bucketName, id + "/");
            return versionsResponse.Versions.FindAll(x => x.IsLatest)
                                   .Where(x => !x.Key.EndsWith("/"))
                                   .Select(x => new S3ObjectVersion { Key = x.Key, VersionId = x.VersionId, LastModified = x.LastModified })
                                   .ToArray();
        }

        public async Task<ObjectDescriptor> GetObjectDescriptor(long id, string versionId)
        {
            string objectVersionId;
            if (string.IsNullOrEmpty(versionId))
            {
                var objectVersions = await GetObjectLatestVersions(id);
                objectVersionId = objectVersions.Where(x => x.Key.EndsWith(Tokens.ObjectPostfix))
                                                .Select(x => x.VersionId)
                                                .SingleOrDefault();

                if (objectVersionId == null)
                {
                    throw new ObjectNotFoundException($"Object '{id}' not found.");
                }
            }
            else
            {
                objectVersionId = versionId;
            }

            var persistenceDescriptorWrapper = await GetObjectFromS3<ObjectPersistenceDescriptor>(id.AsS3ObjectKey(Tokens.ObjectPostfix), objectVersionId);
            var persistenceDescriptor = (ObjectPersistenceDescriptor)persistenceDescriptorWrapper;

            var elements = new ConcurrentBag<IObjectElementDescriptor>();
            Parallel.ForEach(
                persistenceDescriptor.Elements,
                objectVersion =>
                    {
                        var elementDescriptorWrapper = GetObjectFromS3<ObjectElementDescriptor>(objectVersion.Key, objectVersion.VersionId).Result;
                        var elementDescriptor = (ObjectElementDescriptor)elementDescriptorWrapper;

                        elementDescriptor.Id = objectVersion.Key.AsSubObjectId();
                        elementDescriptor.VersionId = objectVersion.VersionId;
                        elementDescriptor.LastModified = elementDescriptorWrapper.LastModified;

                        elements.Add(elementDescriptor);
                    });

            var descriptor = new ObjectDescriptor
                                 {
                                     Id = id,
                                     VersionId = objectVersionId,
                                     LastModified = persistenceDescriptorWrapper.LastModified,
                                     TemplateId = persistenceDescriptor.TemplateId,
                                     TemplateVersionId = persistenceDescriptor.TemplateVersionId,
                                     Language = persistenceDescriptor.Language,
                                     Author = persistenceDescriptorWrapper.Author,
                                     Properties = persistenceDescriptor.Properties,
                                     Elements = elements
                                 };
            return descriptor;
        }

        public async Task<bool> IsObjectExists(long id)
        {
            var listResponse = await _amazonS3.ListObjectsV2Async(
                                   new ListObjectsV2Request
                                   {
                                       BucketName = _bucketName,
                                       MaxKeys = 1,
                                       Prefix = id.ToString() + "/" + Tokens.ObjectPostfix
                                   });
            return listResponse.S3Objects.Count != 0;
        }

        private async Task<ObjectWrapper<T>> GetObjectFromS3<T>(string key, string versionId)
        {
            GetObjectResponse getObjectResponse;
            try
            {
                getObjectResponse = await _amazonS3.GetObjectAsync(_bucketName, key, versionId);
            }
            catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ObjectNotFoundException($"Object '{key}' with versionId '{versionId}' not found.");
            }

            var metadataWrapper = MetadataCollectionWrapper.For(getObjectResponse.Metadata);
            var author = metadataWrapper.Read<string>(MetadataElement.Author);

            string content;
            using (var reader = new StreamReader(getObjectResponse.ResponseStream, Encoding.UTF8))
            {
                content = reader.ReadToEnd();
            }

            var obj = JsonConvert.DeserializeObject<T>(content, SerializerSettings.Default);
            return new ObjectWrapper<T>(obj, author, getObjectResponse.LastModified);
        }

        private class ObjectWrapper<T>
        {
            private readonly T _object;

            public ObjectWrapper(T @object, string author, DateTime lastModified)
            {
                _object = @object;
                Author = author;
                LastModified = lastModified;
            }

            public string Author { get; }
            public DateTime LastModified { get; }

            public static implicit operator T(ObjectWrapper<T> wrapper)
            {
                return wrapper._object;
            }
        }
    }
}