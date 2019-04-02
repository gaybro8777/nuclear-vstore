using System.Collections.Generic;
using System.Linq;

using NuClear.VStore.Descriptors.Templates;

namespace NuClear.VStore.Descriptors.Sessions
{
    public static class TemplateDescriptorExtensions
    {
        public static IEnumerable<TElementDescriptor> GetBinaryElements<TElementDescriptor>(this IReadOnlyCollection<TElementDescriptor> templateDescriptor)
            where TElementDescriptor : IElementDescriptor
            => templateDescriptor.Where(x => x.Type == ElementDescriptorType.Article ||
                                             x.Type == ElementDescriptorType.BitmapImage ||
                                             x.Type == ElementDescriptorType.VectorImage ||
                                             x.Type == ElementDescriptorType.CompositeBitmapImage ||
                                             x.Type == ElementDescriptorType.ScalableBitmapImage);

        public static IReadOnlyCollection<int> GetBinaryElementTemplateCodes(this ITemplateDescriptor templateDescriptor) =>
            templateDescriptor.Elements
                              .GetBinaryElements()
                              .Select(x => x.TemplateCode)
                              .ToList();
    }
}