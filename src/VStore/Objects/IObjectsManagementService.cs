using System.Collections.Generic;
using System.Threading.Tasks;

using NuClear.VStore.DataContract;
using NuClear.VStore.Descriptors.Objects;

namespace NuClear.VStore.Objects
{
    public interface IObjectsManagementService
    {
        Task<string> Create(long id, AuthorInfo authorInfo, IObjectDescriptor objectDescriptor);
        Task<string> Modify(long id, string versionId, AuthorInfo authorInfo, IObjectDescriptor modifiedObjectDescriptor);
        Task<string> Upgrade(long id, string versionId, AuthorInfo authorInfo, IReadOnlyCollection<int> modifiedElementsTemplateCodes, IObjectDescriptor objectDescriptor);
    }
}