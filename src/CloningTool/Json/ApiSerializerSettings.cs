using System.Globalization;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

using NuClear.VStore.Json;

namespace CloningTool.Json
{
    internal static class ApiSerializerSettings
    {
        private static readonly JsonConverter[] CustomConverters =
            {
                new StringEnumConverter { NamingStrategy = new CamelCaseNamingStrategy() },
                new ElementDescriptorCollectionJsonConverter(),
                new ApiObjectElementDescriptorJsonConverter(),
                new ApiTemplateDescriptorJsonConverter(),
                new ApiTemplateElementDescriptorJsonConverter(),
                new ElementDescriptorJsonConverter(),
                new RemarkJsonConverter()
            };

        static ApiSerializerSettings()
        {
            Default = new JsonSerializerSettings
                {
                    Culture = CultureInfo.InvariantCulture,
                    ContractResolver = new CamelCasePropertyNamesContractResolver()
                };

            for (var index = 0; index < CustomConverters.Length; ++index)
            {
                Default.Converters.Insert(index, CustomConverters[index]);
            }
        }

        public static JsonSerializerSettings Default { get; }
    }
}
