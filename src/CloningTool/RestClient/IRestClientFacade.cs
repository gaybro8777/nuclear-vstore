using System;
using System.Net.Http.Headers;
using System.Threading.Tasks;

using CloningTool.Json;

namespace CloningTool.RestClient
{
    public interface IRestClientFacade : IReadOnlyRestClientFacade
    {
        Task CreatePositionTemplateLinkAsync(string positionId, string templateId);
        Task<string> CreateTemplateAsync(long id, ApiTemplateDescriptor template);
        Task<string> UpdateTemplateAsync(ApiTemplateDescriptor template, string versionId);
        Task<ApiObjectDescriptor> CreateAdvertisementPrototypeAsync(long templateId, string templateVersion, string langCode, long firmId);
        Task<ApiObjectElementRawValue> UploadFileAsync(long id, Uri uploadUri, string fileName, byte[] fileData, MediaTypeHeaderValue contentType, params NameValueHeaderValue[] headers);
        Task UpdateAdvertisementModerationStatusAsync(long id, string versionId, ModerationResult moderationResult);
        Task SelectAdvertisementToWhitelistAsync(long id);
        Task<string> CreateAdvertisementAsync(long id, long firmId, ApiObjectDescriptor advertisement);
        Task<ApiObjectDescriptor> UpdateAdvertisementAsync(ApiObjectDescriptor advertisement);
        Task CreateRemarkCategoryAsync(string remarkCategoryId, RemarkCategory remarkCategory);
        Task UpdateRemarkCategoryAsync(string remarkCategoryId, RemarkCategory remarkCategory);
        Task CreateRemarkAsync(string remarkId, Remark remark);
        Task UpdateRemarkAsync(string remarkId, Remark remark);
    }
}
