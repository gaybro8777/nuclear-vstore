using System;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Threading.Tasks;

using CloningTool.Json;

using NuClear.VStore.DataContract;

namespace CloningTool.RestClient
{
    public interface IReadOnlyRestClientFacade
    {
        Task<IReadOnlyCollection<PositionDescriptor>> GetContentPositionsAsync();
        Task<ApiTemplateDescriptor> GetTemplateAsync(long id, string versionId = null);
        Task<IReadOnlyCollection<TemplateVersionRecord>> GetTemplateVersionsAsync(long id);
        Task<IReadOnlyCollection<ApiListTemplate>> GetTemplatesAsync();
        Task<IReadOnlyCollection<RemarkCategory>> GetRemarkCategoriesAsync();
        Task<IReadOnlyCollection<Remark>> GetRemarksAsync();
        Task<IReadOnlyCollection<ApiListAdvertisement>> GetAdvertisementsByTemplateAsync(long templateId, int? fetchSize);
        Task<IReadOnlyCollection<ApiListAdvertisement>> GetAdvertisementsByIdsAsync(IEnumerable<long> ids);
        Task<ApiObjectDescriptor> GetAdvertisementAsync(long id, string versionId = null);
        Task<IReadOnlyCollection<ApiObjectVersion>> GetAdvertisementVersionsAsync(long id);
        Task<(byte[] data, MediaTypeHeaderValue contentType)> DownloadFileAsync(long advertisementId, Uri downloadUrl);
        Task EnsureApiAvailableAsync(int initialPingInterval, int initialPingTries);
    }
}
