using System;
using System.Collections.Generic;

using Newtonsoft.Json.Linq;

using NuClear.VStore.Descriptors.Objects;

namespace NuClear.VStore.DataContract
{
    public sealed class ObjectVersionRecord : ObjectVersionMetadataRecord
    {
        public ObjectVersionRecord(
            long id,
            string versionId,
            int versionIndex,
            long templateId,
            string templateVersionId,
            DateTime lastModified,
            AuthorInfo authorInfo,
            JObject properties,
            IReadOnlyCollection<ElementRecord> elements,
            IReadOnlyCollection<int> modifiedElements)
            : base(id, versionId, versionIndex, templateId, templateVersionId, lastModified, authorInfo, properties, modifiedElements)
        {
            Elements = elements;
        }

        public IReadOnlyCollection<ElementRecord> Elements { get; set; }

        public sealed class ElementRecord
        {
            public ElementRecord(int templateCode, IObjectElementValue value)
            {
                TemplateCode = templateCode;
                Value = value;
            }

            public int TemplateCode { get; }
            public IObjectElementValue Value { get; }
        }
    }
}