using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

using CloningTool.Json;

using Microsoft.Extensions.Logging;

using Newtonsoft.Json;

using NuClear.VStore.DataContract;
using NuClear.VStore.Http;
using NuClear.VStore.Objects;

namespace CloningTool.RestClient
{
    public class OkapiClient : IRestClientFacade, IDisposable
    {
        private const int ApiFetchMaxSize = 500;
        private const string DefaultServer = "okapi";
        private const string PaginationTotalCountHeaderName = "X-Pagination-Total-Count";

        private readonly Uri _apiUri;
        private readonly Uri _templateUri;
        private readonly Uri _amUri;
        private readonly Uri _positionUri;
        private readonly Uri _searchUri;
        private readonly Uri _remarkUri;
        private readonly Uri _remarkCategoryUri;
        private readonly ILogger<OkapiClient> _logger;
        private readonly HttpClient _authorizedHttpClient = new HttpClient();
        private readonly HttpClient _unauthorizedHttpClient = new HttpClient();

        public OkapiClient(ILogger<OkapiClient> logger, Uri apiUri, string apiVersion, string apiToken)
        {
            _apiUri = apiUri;
            var apiBase = new Uri(apiUri, $"api/{apiVersion}/");
            _templateUri = new Uri(apiBase, "template/");
            _positionUri = new Uri(apiBase, "nomenclature/");
            _searchUri = new Uri(apiBase, "search/");
            _remarkUri = new Uri(apiBase, "remark/");
            _remarkCategoryUri = new Uri(apiBase, "remarkCategory/");
            _amUri = new Uri(apiBase, "am/");
            _logger = logger;
            _authorizedHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        }

        public void Dispose()
        {
            _authorizedHttpClient?.Dispose();
            _unauthorizedHttpClient?.Dispose();
        }

        public async Task<string> CreateAdvertisementAsync(long id, long firmId, ApiObjectDescriptor advertisement)
        {
            var amId = id.ToString();
            var methodUri = new Uri(_amUri, amId + "?firm=" + firmId.ToString());
            var server = string.Empty;
            var requestId = string.Empty;
            var stringResponse = string.Empty;
            try
            {
                using (var content = new StringContent(JsonConvert.SerializeObject(advertisement, ApiSerializerSettings.Default), Encoding.UTF8, ContentType.Json))
                {
                    using (var response = await _authorizedHttpClient.PutAsync(methodUri, content))
                    {
                        (stringResponse, server, requestId) = await HandleResponse(response);
                        if (response.StatusCode == HttpStatusCode.Conflict)
                        {
                            throw new ObjectAlreadyExistsException(id);
                        }

                        response.EnsureSuccessStatusCode();
                        var res = JsonConvert.DeserializeObject<ApiVersionedDescriptor>(stringResponse, ApiSerializerSettings.Default);
                        if (res == null)
                        {
                            throw new SerializationException("Cannot deserialize response: " + stringResponse);
                        }

                        _logger.LogInformation("Created advertisement {id} got version: {version}", amId, res.VersionId);

                        return res.VersionId;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Request {requestId} to server {server} error while advertisement {id} creating with response: {response}",
                    requestId,
                    server,
                    amId,
                    stringResponse);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Advertisement {id} creating error with response: {response}", amId, stringResponse);
                throw;
            }
        }

        public async Task<ApiObjectDescriptor> GetAdvertisementAsync(long id, string versionId = null)
        {
            var methodUri = new Uri(_amUri, id.ToString() + (string.IsNullOrEmpty(versionId) ? string.Empty : $"/version/{versionId}"));
            var res = await GetEntityAsync<IReadOnlyCollection<ApiObjectDescriptor>>("advertisement", methodUri, id, versionId);
            if (res.Count != 1)
            {
                throw new NotSupportedException("Unsupported elements count in response for advertisement " + id + ": " + res.Count.ToString());
            }

            return res.First();
        }

        public async Task<IReadOnlyCollection<ApiObjectVersion>> GetAdvertisementVersionsAsync(long id)
            => await GetEntityVersionsAsync<ApiObjectVersion>(id, _amUri, "advertisement");

        public async Task<ApiObjectDescriptor> UpdateAdvertisementAsync(ApiObjectDescriptor advertisement)
        {
            var server = string.Empty;
            var requestId = string.Empty;
            var stringResponse = string.Empty;
            var advertisementId = advertisement.Id.ToString();
            var methodUri = new Uri(_amUri, advertisementId);
            try
            {
                using (var content = new StringContent(JsonConvert.SerializeObject(advertisement, ApiSerializerSettings.Default), Encoding.UTF8, ContentType.Json))
                {
                    using (var request = new HttpRequestMessage(new HttpMethod("PATCH"), methodUri))
                    {
                        request.Content = content;
                        using (var response = await _authorizedHttpClient.SendAsync(request))
                        {
                            (stringResponse, server, requestId) = await HandleResponse(response);
                            response.EnsureSuccessStatusCode();
                            var descriptor = JsonConvert.DeserializeObject<ApiObjectDescriptor>(stringResponse, ApiSerializerSettings.Default);
                            if (descriptor == null)
                            {
                                throw new SerializationException("Cannot deserialize object descriptor " + advertisementId + ": " + stringResponse);
                            }

                            _logger.LogInformation(
                                "Updated advertisement {id} got new version: {version} (old version {oldVersion})",
                                advertisementId,
                                descriptor.VersionId,
                                advertisement.VersionId);

                            return descriptor;
                        }
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Request {requestId} to server {server} error while updating advertisement {id} with response: {response}",
                    requestId,
                    server,
                    advertisementId,
                    stringResponse);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Advertisement {id} update error", advertisementId);
                throw;
            }
        }

        public async Task CreateRemarkCategoryAsync(string remarkCategoryId, RemarkCategory remarkCategory) =>
            await ModifyEntityStateAsync(HttpMethod.Post, new Uri(_remarkCategoryUri, remarkCategoryId), remarkCategoryId, remarkCategory);

        public async Task UpdateRemarkCategoryAsync(string remarkCategoryId, RemarkCategory remarkCategory) =>
            await ModifyEntityStateAsync(HttpMethod.Put, new Uri(_remarkCategoryUri, remarkCategoryId), remarkCategoryId, remarkCategory);

        public async Task CreateRemarkAsync(string remarkId, Remark remark) =>
            await ModifyEntityStateAsync(HttpMethod.Post, new Uri(_remarkUri, remarkId), remarkId, remark);

        public async Task UpdateRemarkAsync(string remarkId, Remark remark) =>
            await ModifyEntityStateAsync(HttpMethod.Put, new Uri(_remarkUri, remarkId), remarkId, remark);

        public async Task<ApiObjectDescriptor> CreateAdvertisementPrototypeAsync(long templateId, string templateVersion, string langCode, long firmId)
        {
            var methodUri = new Uri(_templateUri, $"{templateId}/{templateVersion}/session?languages={langCode}&firm={firmId}");
            using (var req = new HttpRequestMessage(HttpMethod.Post, methodUri))
            {
                var server = string.Empty;
                var requestId = string.Empty;
                var stringResponse = string.Empty;
                try
                {
                    using (var response = await _authorizedHttpClient.SendAsync(req))
                    {
                        (stringResponse, server, requestId) = await HandleResponse(response);
                        response.EnsureSuccessStatusCode();
                        var descriptor = JsonConvert.DeserializeObject<IReadOnlyCollection<ApiObjectDescriptor>>(stringResponse, ApiSerializerSettings.Default);
                        if (descriptor == null)
                        {
                            throw new SerializationException("Cannot deserialize new object descriptor for template " + templateId + ": " + stringResponse);
                        }

                        return descriptor.First();
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(
                        ex,
                        "Request {requestId} to server {server} error while getting new object for template {id} and lang {lang} with response: {response}",
                        requestId,
                        server,
                        templateId,
                        langCode,
                        stringResponse);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Get new object for template {id} and lang {lang} error", templateId, langCode);
                    throw;
                }
            }
        }

        public async Task<IReadOnlyCollection<Remark>> GetRemarksAsync()
        {
            var server = string.Empty;
            var requestId = string.Empty;
            var stringResponse = string.Empty;
            try
            {
                using (var response = await _authorizedHttpClient.GetAsync(_remarkUri))
                {
                    (stringResponse, server, requestId) = await HandleResponse(response);
                    response.EnsureSuccessStatusCode();
                    var remarks = JsonConvert.DeserializeObject<IReadOnlyCollection<Remark>>(stringResponse, ApiSerializerSettings.Default);
                    if (remarks == null)
                    {
                        throw new SerializationException("Cannot deserialize remarks: " + stringResponse);
                    }

                    return remarks;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Request {requestId} to server {server} error while getting remarks with response: {response}",
                    requestId,
                    server,
                    stringResponse);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Get remarks error");
                throw;
            }
        }

        public async Task<IReadOnlyCollection<RemarkCategory>> GetRemarkCategoriesAsync()
        {
            var server = string.Empty;
            var requestId = string.Empty;
            var stringResponse = string.Empty;
            try
            {
                using (var response = await _authorizedHttpClient.GetAsync(_remarkCategoryUri))
                {
                    (stringResponse, server, requestId) = await HandleResponse(response);
                    response.EnsureSuccessStatusCode();
                    var categories = JsonConvert.DeserializeObject<IReadOnlyCollection<RemarkCategory>>(stringResponse, ApiSerializerSettings.Default);
                    if (categories == null)
                    {
                        throw new SerializationException("Cannot deserialize remark categories: " + stringResponse);
                    }

                    return categories;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Request {requestId} to server {server} error while getting remark categories with response: {response}",
                    requestId,
                    server,
                    stringResponse);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Get remark categories error");
                throw;
            }
        }

        public async Task<IReadOnlyCollection<ApiListAdvertisement>> GetAdvertisementsByTemplateAsync(long templateId, int? fetchSize)
        {
            var server = string.Empty;
            var requestId = string.Empty;
            var stringResponse = string.Empty;
            var allDescriptors = new List<ApiListAdvertisement>();
            try
            {
                for (var pageNum = 1; ; ++pageNum)
                {
                    var methodUri = new Uri(_searchUri, $"am?template={templateId}&count={fetchSize ?? ApiFetchMaxSize}&page={pageNum}&sort=createdAt:desc");
                    using (var response = await _authorizedHttpClient.GetAsync(methodUri))
                    {
                        (stringResponse, server, requestId) = await HandleResponse(response);
                        response.EnsureSuccessStatusCode();
                        var descriptors = JsonConvert.DeserializeObject<IReadOnlyCollection<ApiListAdvertisement>>(stringResponse, ApiSerializerSettings.Default);
                        if (descriptors == null)
                        {
                            throw new SerializationException("Cannot deserialize advertisements: " + stringResponse);
                        }

                        if (descriptors.Count < 1 && pageNum == 1)
                        {
                            _logger.LogWarning("There are no advertisements with template {id}", templateId);
                            return Array.Empty<ApiListAdvertisement>();
                        }

                        if (fetchSize.HasValue)
                        {
                            return descriptors;
                        }

                        allDescriptors.AddRange(descriptors);

                        if (response.Headers.TryGetValues(PaginationTotalCountHeaderName, out var values) &&
                            int.TryParse(values.FirstOrDefault(), out var count) &&
                            count == allDescriptors.Count)
                        {
                            break;
                        }
                    }
                }

                return allDescriptors;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Request {requestId} to server {server} error while getting advertisements with response: {response}",
                    requestId,
                    server,
                    stringResponse);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Get advertisements error");
                throw;
            }
        }

        public async Task<IReadOnlyCollection<ApiListAdvertisement>> GetAdvertisementsByIdsAsync(IEnumerable<long> ids)
        {
            var server = string.Empty;
            var requestId = string.Empty;
            var stringResponse = string.Empty;
            var allDescriptors = new List<ApiListAdvertisement>();
            var idsList = string.Join(",", ids);
            try
            {
                for (var pageNum = 1; ; ++pageNum)
                {
                    var methodUri = new Uri(_searchUri, $"am?id={idsList}&count={ApiFetchMaxSize}&page={pageNum}&sort=createdAt:desc");
                    using (var response = await _authorizedHttpClient.GetAsync(methodUri))
                    {
                        (stringResponse, server, requestId) = await HandleResponse(response);
                        response.EnsureSuccessStatusCode();
                        var descriptors = JsonConvert.DeserializeObject<IReadOnlyCollection<ApiListAdvertisement>>(stringResponse, ApiSerializerSettings.Default);
                        if (descriptors == null)
                        {
                            throw new SerializationException("Cannot deserialize advertisements: " + stringResponse);
                        }

                        if (descriptors.Count < 1 && pageNum == 1)
                        {
                            _logger.LogWarning("There are no advertisements with such ids");
                            return Array.Empty<ApiListAdvertisement>();
                        }

                        allDescriptors.AddRange(descriptors);

                        if (response.Headers.TryGetValues(PaginationTotalCountHeaderName, out var values) &&
                            int.TryParse(values.FirstOrDefault(), out var count) &&
                            count == allDescriptors.Count)
                        {
                            break;
                        }
                    }
                }

                return allDescriptors;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Request {requestId} to server {server} error while getting advertisements with response: {response}",
                    requestId,
                    server,
                    stringResponse);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Get advertisements error");
                throw;
            }
        }

        public async Task SelectAdvertisementToWhitelistAsync(long id)
        {
            var methodUri = new Uri(_amUri, $"{id.ToString()}/whiteList");
            using (var req = new HttpRequestMessage(HttpMethod.Post, methodUri))
            {
                var server = string.Empty;
                var requestId = string.Empty;
                var stringResponse = string.Empty;
                try
                {
                    using (var response = await _authorizedHttpClient.SendAsync(req))
                    {
                        (stringResponse, server, requestId) = await HandleResponse(response);
                        response.EnsureSuccessStatusCode();
                        _logger.LogInformation("Advertisement {id} has been selected to whitelist", id);
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(
                        ex,
                        "Request {requestId} to server {server} error while selecting advertisement {id} to whitelist with response: {response}",
                        requestId,
                        server,
                        id,
                        stringResponse);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while selecting advertisement {id} to whitelist", id);
                    throw;
                }
            }
        }

        public async Task UpdateAdvertisementModerationStatusAsync(long id, string versionId, ModerationResult moderationResult)
        {
            var methodUri = new Uri(_amUri, $"{id}/version/{versionId}/moderation");
            using (var content = new StringContent(JsonConvert.SerializeObject(moderationResult, ApiSerializerSettings.Default), Encoding.UTF8, ContentType.Json))
            {
                using (var req = new HttpRequestMessage(HttpMethod.Put, methodUri))
                {
                    req.Content = content;
                    var server = string.Empty;
                    var requestId = string.Empty;
                    var stringResponse = string.Empty;
                    try
                    {
                        using (var response = await _authorizedHttpClient.SendAsync(req))
                        {
                            (stringResponse, server, requestId) = await HandleResponse(response);
                            response.EnsureSuccessStatusCode();
                            _logger.LogInformation(
                                "Advertisement {id} version {versionId} has been moderated with resolution {resolution}",
                                id,
                                versionId,
                                moderationResult.Resolution);
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogError(
                            ex,
                            "Request {requestId} to server {server} error while moderate advertisement {id} version {versionId} with resolution {resolution}; response: {response}",
                            requestId,
                            server,
                            id,
                            versionId,
                            moderationResult.Resolution,
                            stringResponse);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Error while moderate advertisement {id} version {versionId} with resolution {resolution}",
                            id,
                            versionId,
                            moderationResult.Resolution);
                        throw;
                    }
                }
            }
        }

        public async Task<string> CreateTemplateAsync(long id, ApiTemplateDescriptor template)
        {
            var methodUri = new Uri(_templateUri, id.ToString());
            var server = string.Empty;
            var requestId = string.Empty;
            var stringResponse = string.Empty;
            try
            {
                using (var content = new StringContent(JsonConvert.SerializeObject(template, ApiSerializerSettings.Default), Encoding.UTF8, ContentType.Json))
                {
                    using (var response = await _authorizedHttpClient.PostAsync(methodUri, content))
                    {
                        (stringResponse, server, requestId) = await HandleResponse(response);
                        response.EnsureSuccessStatusCode();

                        var newVersion = response.Headers.ETag.Tag.Trim('"');
                        _logger.LogInformation("Created template {id} got version: {version}", id, newVersion);
                        return newVersion;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Request {requestId} to server {server} error while creating template {id} with response: {response}",
                    requestId,
                    server,
                    id,
                    stringResponse);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Template {id} creating error", id);
                throw;
            }
        }

        public async Task<IReadOnlyCollection<TemplateVersionRecord>> GetTemplateVersionsAsync(long id) =>
            await GetEntityVersionsAsync<TemplateVersionRecord>(id, _templateUri, "template");

        public async Task<IReadOnlyCollection<ApiListTemplate>> GetTemplatesAsync()
        {
            var server = string.Empty;
            var requestId = string.Empty;
            var stringResponse = string.Empty;
            var allDescriptors = new List<ApiListTemplate>();
            try
            {
                for (var pageNum = 1; ; ++pageNum)
                {
                    var methodUri = new Uri(_searchUri, $"template?count={ApiFetchMaxSize}&page={pageNum}");
                    using (var response = await _authorizedHttpClient.GetAsync(methodUri))
                    {
                        (stringResponse, server, requestId) = await HandleResponse(response);
                        response.EnsureSuccessStatusCode();
                        var descriptors = JsonConvert.DeserializeObject<IReadOnlyCollection<ApiListTemplate>>(stringResponse, ApiSerializerSettings.Default);
                        if (descriptors == null)
                        {
                            throw new SerializationException("Cannot deserialize templates: " + stringResponse);
                        }

                        if (descriptors.Count < 1)
                        {
                            break;
                        }

                        allDescriptors.AddRange(descriptors);

                        if (response.Headers.TryGetValues(PaginationTotalCountHeaderName, out var values) &&
                            int.TryParse(values.FirstOrDefault(), out var count) &&
                            count == allDescriptors.Count)
                        {
                            break;
                        }
                    }
                }

                return allDescriptors;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Request {requestId} to server {server} error while getting templates with response: {response}",
                    requestId,
                    server,
                    stringResponse);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Get templates error");
                throw;
            }
        }

        public async Task<ApiTemplateDescriptor> GetTemplateAsync(long id, string versionId = null)
        {
            var templateIdentifier = id.ToString() + (string.IsNullOrEmpty(versionId) ? string.Empty : $"/{versionId}");
            var methodUri = new Uri(_templateUri, templateIdentifier);
            return await GetEntityAsync<ApiTemplateDescriptor>("template", methodUri, id, versionId);
        }

        public async Task<string> UpdateTemplateAsync(ApiTemplateDescriptor template, string versionId)
        {
            var server = string.Empty;
            var requestId = string.Empty;
            var stringResponse = string.Empty;
            var templateId = template.Id.ToString();
            var methodUri = new Uri(_templateUri, templateId);
            try
            {
                using (var content = new StringContent(JsonConvert.SerializeObject(template, ApiSerializerSettings.Default), Encoding.UTF8, ContentType.Json))
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Put, methodUri))
                    {
                        request.Content = content;
                        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{versionId}\""));
                        using (var response = await _authorizedHttpClient.SendAsync(request))
                        {
                            (stringResponse, server, requestId) = await HandleResponse(response);
                            response.EnsureSuccessStatusCode();
                            var newVersion = response.Headers.ETag.Tag.Trim('"');
                            _logger.LogInformation(
                                "Updated template {id} got new version: {version} (old version {oldVersion})",
                                templateId,
                                newVersion,
                                versionId);

                            return newVersion;
                        }
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Request {requestId} to server {server} error while updating template {id} with response: {response}",
                    requestId,
                    server,
                    templateId,
                    stringResponse);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Template {id} update error", templateId);
                throw;
            }
        }

        public async Task<IReadOnlyCollection<PositionDescriptor>> GetContentPositionsAsync()
        {
            var server = string.Empty;
            var requestId = string.Empty;
            var stringResponse = string.Empty;
            var allDescriptors = new List<PositionDescriptor>();
            try
            {
                for (var pageNum = 1; ; ++pageNum)
                {
                    var methodUri = new Uri(_searchUri, $"nomenclature?isDeleted=false&isContentSales=true&count={ApiFetchMaxSize}&page={pageNum}");
                    using (var response = await _authorizedHttpClient.GetAsync(methodUri))
                    {
                        (stringResponse, server, requestId) = await HandleResponse(response);
                        response.EnsureSuccessStatusCode();
                        var descriptors = JsonConvert.DeserializeObject<IReadOnlyCollection<PositionDescriptor>>(stringResponse, ApiSerializerSettings.Default);
                        if (descriptors == null)
                        {
                            throw new SerializationException("Cannot deserialize positions: " + stringResponse);
                        }

                        if (descriptors.Count < 1)
                        {
                            break;
                        }

                        allDescriptors.AddRange(descriptors);

                        if (response.Headers.TryGetValues(PaginationTotalCountHeaderName, out var values) &&
                            int.TryParse(values.FirstOrDefault(), out var count) &&
                            count == allDescriptors.Count)
                        {
                            break;
                        }
                    }
                }

                return allDescriptors;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Request {requestId} to server {server} error while getting positions with response: {response}",
                    requestId,
                    server,
                    stringResponse);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Get positions error");
                throw;
            }
        }

        public async Task CreatePositionTemplateLinkAsync(string positionId, string templateId)
        {
            var methodUri = new Uri(_positionUri, $"{positionId}/template/{templateId}");
            using (var req = new HttpRequestMessage(HttpMethod.Post, methodUri))
            {
                var server = string.Empty;
                var requestId = string.Empty;
                var stringResponse = string.Empty;
                try
                {
                    using (var response = await _authorizedHttpClient.SendAsync(req))
                    {
                        (stringResponse, server, requestId) = await HandleResponse(response);
                        response.EnsureSuccessStatusCode();
                        _logger.LogInformation("Link has been created between position {positionId} and template {templateId}", positionId, templateId);
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(
                        ex,
                        "Request {requestId} to server {server} error while creating link between position {positionId} and template {templateId} with response: {response}",
                        requestId,
                        server,
                        positionId,
                        templateId,
                        stringResponse);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while creating link between position {positionId} and template {templateId}", positionId, templateId);
                    throw;
                }
            }
        }

        public async Task<(byte[] data, MediaTypeHeaderValue contentType)> DownloadFileAsync(long advertisementId, Uri downloadUrl)
        {
            var stringResponse = string.Empty;
            try
            {
                using (var response = await _unauthorizedHttpClient.GetAsync(downloadUrl))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("File within advertisement {amId} has been downloaded from {url}", advertisementId, downloadUrl);
                        return (await response.Content.ReadAsByteArrayAsync(), response.Content.Headers.ContentType);
                    }

                    (stringResponse, _, _) = await HandleResponse(response);
                    throw new HttpRequestException($"Request error with status {response.StatusCode} and reason {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    default,
                    ex,
                    "File within advertisement {amId} download error from {url}, response: {response}",
                    advertisementId,
                    downloadUrl,
                    stringResponse);
                throw;
            }
        }

        public async Task<ApiObjectElementRawValue> UploadFileAsync(
            long advertisementId,
            Uri uploadUrl,
            string fileName,
            byte[] fileData,
            MediaTypeHeaderValue contentType,
            params NameValueHeaderValue[] headers)
        {
            var url = uploadUrl;
            var stringResponse = string.Empty;
            if (string.IsNullOrEmpty(fileName))
            {
                throw new ArgumentNullException(nameof(fileName), "File name cannot be empty");
            }

            try
            {
                using (var content = new MultipartFormDataContent())
                {
                    using (var memoryStream = new MemoryStream(fileData, false))
                    {
                        using (var streamContent = new StreamContent(memoryStream))
                        {
                            streamContent.Headers.ContentType = contentType;
                            content.Add(streamContent, fileName, fileName);
                            using (var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content })
                            {
                                foreach (var header in headers)
                                {
                                    request.Headers.Add(header.Name, header.Value);
                                }

                                using (var response = await _unauthorizedHttpClient.SendAsync(request))
                                {
                                    stringResponse = (await HandleResponse(response)).ResponseContent;
                                    response.EnsureSuccessStatusCode();
                                    var rawValue = JsonConvert.DeserializeObject<ApiObjectElementRawValue>(stringResponse, ApiSerializerSettings.Default);
                                    _logger.LogInformation(
                                        "File {fileName} with content type {contentType} within advertisement {objectId} has been uploaded to {url} and got value {rawValue}. Headers: {headers}",
                                        fileName,
                                        contentType,
                                        advertisementId,
                                        url,
                                        rawValue.Raw,
                                        headers);

                                    return rawValue;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    default,
                    ex,
                    "File {fileName} within advertisement {objectId} upload error to {url}, response: {response}",
                    fileName,
                    advertisementId,
                    url,
                    stringResponse);
                throw;
            }
        }

        public async Task EnsureApiAvailableAsync(int pingInterval, int pingTries)
        {
            var tryNum = 0;
            var succeeded = false;
            var healthcheckUri = new Uri(_apiUri, "healthcheck");
            do
            {
                ++tryNum;
                _logger.LogInformation("Waiting for {delay} seconds before try {try}", pingInterval, tryNum);
                await Task.Delay(TimeSpan.FromSeconds(pingInterval));
                try
                {
                    _logger.LogInformation("Connecting to {url}", healthcheckUri);
                    using (var response = await _unauthorizedHttpClient.GetAsync(healthcheckUri))
                    {
                        response.EnsureSuccessStatusCode();
                        succeeded = true;
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(default, ex, "Attempt {try} failed while connecting", tryNum);
                }
            }
            while (!succeeded && tryNum < pingTries);

            if (!succeeded)
            {
                throw new WebException("Can't establish connection with API");
            }
        }

        private async Task ModifyEntityStateAsync<T>(HttpMethod httpMethod, Uri methodUri, string entityId, T entity)
        {
            var server = string.Empty;
            var requestId = string.Empty;
            var stringResponse = string.Empty;
            try
            {
                using (var content = new StringContent(JsonConvert.SerializeObject(entity, ApiSerializerSettings.Default), Encoding.UTF8, ContentType.Json))
                {
                    var request = new HttpRequestMessage(httpMethod, methodUri) { Content = content };
                    using (var response = await _authorizedHttpClient.SendAsync(request))
                    {
                        (stringResponse, server, requestId) = await HandleResponse(response);
                        response.EnsureSuccessStatusCode();
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Request {requestId} to server {server} error while creating {entityType} {id} with response: {response}",
                    requestId,
                    server,
                    typeof(T).Name,
                    entityId,
                    stringResponse);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{entityType} {id} creating error", typeof(T).Name, entityId);
                throw;
            }
        }

        private async Task<T> GetEntityAsync<T>(string entityType, Uri methodUri, long id, string versionId)
            where T : class
        {
            var server = string.Empty;
            var requestId = string.Empty;
            var stringResponse = string.Empty;
            var entityId = id.ToString() + (string.IsNullOrEmpty(versionId) ? string.Empty : $"/{versionId}");
            try
            {
                using (var response = await _authorizedHttpClient.GetAsync(methodUri))
                {
                    (stringResponse, server, requestId) = await HandleResponse(response);
                    if (response.StatusCode == HttpStatusCode.NotFound && server == DefaultServer)
                    {
                        _logger.LogInformation("The {entity} {id} not found", entityType, entityId);
                        return null;
                    }

                    response.EnsureSuccessStatusCode();
                    var res = JsonConvert.DeserializeObject<T>(stringResponse, ApiSerializerSettings.Default);
                    if (res == null)
                    {
                        throw new SerializationException($"Cannot deserialize response for {entityType} {entityId}: {stringResponse}");
                    }

                    return res;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Request {requestId} to server {server} error while getting {entity} {id} with response: {response}",
                    requestId,
                    server,
                    entityType,
                    entityId,
                    stringResponse);

                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Getting {entity} {id} error", entityType, entityId);
                throw;
            }
        }

        private async Task<IReadOnlyCollection<T>> GetEntityVersionsAsync<T>(long id, Uri baseUri, string entityName)
        {
            var server = string.Empty;
            var requestId = string.Empty;
            var stringResponse = string.Empty;
            var methodUri = new Uri(baseUri, id.ToString() + "/version");
            try
            {
                using (var response = await _authorizedHttpClient.GetAsync(methodUri))
                {
                    (stringResponse, server, requestId) = await HandleResponse(response);
                    if (response.StatusCode == HttpStatusCode.NotFound &&
                        server == DefaultServer)
                    {
                        _logger.LogInformation("The {entity} {id} not found", entityName, id);
                        return Array.Empty<T>();
                    }

                    response.EnsureSuccessStatusCode();
                    var versions = JsonConvert.DeserializeObject<IReadOnlyCollection<T>>(stringResponse, ApiSerializerSettings.Default);
                    if (versions == null || versions.Count < 1)
                    {
                        throw new SerializationException($"Cannot deserialize {entityName} {id.ToString()} versions: {stringResponse}");
                    }

                    return versions;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "Request {requestId} to server {server} error while getting {entity} {id} versions with response: {response}",
                    requestId,
                    server,
                    entityName,
                    id,
                    stringResponse);

                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(default, ex, "Get {entity} {id} versions error", entityName, id);
                throw;
            }
        }

        private async Task<(string ResponseContent, string Server, string RequestId)> HandleResponse(HttpResponseMessage response)
        {
            var stringResponse = await response.Content.ReadAsStringAsync();
            response.Headers.TryGetValues(HeaderNames.Server, out var server);
            response.Headers.TryGetValues(HeaderNames.RequestId, out var requestId);
            _logger.LogDebug(
                "Sent '{method}' request on '{url}', request id {requestId}, server {server}, got status {status} with response: {response}",
                response.RequestMessage.Method,
                response.RequestMessage.RequestUri,
                requestId,
                server,
                response.StatusCode,
                stringResponse);

            return (stringResponse, server?.FirstOrDefault(), requestId?.FirstOrDefault());
        }
    }
}