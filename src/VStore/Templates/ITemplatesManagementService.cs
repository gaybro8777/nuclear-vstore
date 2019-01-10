using System.Collections.Generic;
using System.Threading.Tasks;

using NuClear.VStore.DataContract;
using NuClear.VStore.Descriptors.Templates;

namespace NuClear.VStore.Templates
{
    public interface ITemplatesManagementService
    {
        IReadOnlyCollection<IElementDescriptor> GetAvailableElementDescriptors();
        Task<string> CreateTemplate(long id, AuthorInfo authorInfo, ITemplateDescriptor templateDescriptor);
        Task<string> ModifyTemplate(long id, string versionId, AuthorInfo authorInfo, ITemplateDescriptor templateDescriptor);
        Task VerifyElementDescriptorsConsistency(IEnumerable<IElementDescriptor> elementDescriptors);
    }
}