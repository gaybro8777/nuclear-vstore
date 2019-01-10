using System;
using System.Collections.Generic;
using System.Linq;

using NuClear.VStore.Descriptors.Sessions;
using NuClear.VStore.Descriptors.Templates;

namespace NuClear.VStore.Sessions.Fetch
{
    public sealed class FetchUrl
    {
        private FetchUrl(int templateCode, string relativeUrl)
        {
            TemplateCode = templateCode;
            RelativeUrl = relativeUrl;
        }

        public int TemplateCode { get; }
        public string RelativeUrl { get; }

        public static IReadOnlyCollection<FetchUrl> Generate(ITemplateDescriptor templateDescriptor, Func<int, string> urlComposer)
        {
            return templateDescriptor.GetBinaryElementTemplateCodes()
                                     .Select(x => new FetchUrl(x, urlComposer(x)))
                                     .ToList();
        }
    }
}