using System.Globalization;

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace NuClear.VStore.Json
{
    public static class SerializerSettings
    {
        private static readonly JsonConverter[] CustomConverters =
            {
                new StringEnumConverter { NamingStrategy = new CamelCaseNamingStrategy() },
                new ElementDescriptorJsonConverter(),
                new ElementDescriptorCollectionJsonConverter(),
                new TemplateDescriptorJsonConverter(),
                new ObjectElementPersistenceDescriptorJsonConverter(),
                new ObjectDescriptorJsonConverter()
            };

        static SerializerSettings()
        {
            Default = new JsonSerializerSettings
                          {
                              Culture = CultureInfo.InvariantCulture,
                              ContractResolver = new CamelCasePropertyNamesContractResolver()
                          };
            for (var index = 0; index < CustomConverters.Length; index++)
            {
                Default.Converters.Insert(index, CustomConverters[index]);
            }
        }

        public static JsonSerializerSettings Default { get; }
    }
}