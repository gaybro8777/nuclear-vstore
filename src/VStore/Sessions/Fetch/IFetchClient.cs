using System;
using System.IO;
using System.Threading.Tasks;

namespace NuClear.VStore.Sessions.Fetch
{
    public interface IFetchClient
    {
        /// <summary>
        /// Fetch content from specified <see cref="Uri"/>
        /// </summary>
        /// <param name="fetchUri">Uri to fetch resource from</param>
        /// <exception cref="FetchRequestException">Unsuccessful response status code</exception>
        /// <exception cref="FetchResponseTooLargeException">Response length exceeds allowed maximum</exception>
        /// <exception cref="FetchResponseContentTypeInvalidException">Content type in response is not specified</exception>
        /// <returns>Tuple with content stream and MIME-type</returns>
        Task<(Stream stream, string mediaType)> FetchAsync(Uri fetchUri);
    }
}