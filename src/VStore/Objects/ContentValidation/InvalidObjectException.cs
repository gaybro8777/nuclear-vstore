using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using NuClear.VStore.Json;
using NuClear.VStore.Objects.ContentValidation.Errors;
using NuClear.VStore.Sessions.ContentValidation.Errors;

namespace NuClear.VStore.Objects.ContentValidation
{
    public sealed class InvalidObjectException : Exception
    {
        public InvalidObjectException(
            long objectId,
            IReadOnlyDictionary<int, IReadOnlyCollection<ObjectElementValidationError>> elementErrors,
            IReadOnlyDictionary<int, IReadOnlyCollection<BinaryValidationError>> binaryElementErrors = null)
        {
            ObjectId = objectId;
            ElementErrors = elementErrors;
            BinaryElementErrors = binaryElementErrors ?? new Dictionary<int, IReadOnlyCollection<BinaryValidationError>>();
        }

        public long ObjectId { get; }

        public IReadOnlyDictionary<int, IReadOnlyCollection<ObjectElementValidationError>> ElementErrors { get; }

        public IReadOnlyDictionary<int, IReadOnlyCollection<BinaryValidationError>> BinaryElementErrors { get; }

        public JToken SerializeToJson()
        {
            var elements = new JArray();
            foreach (var templateCode in ElementErrors.Keys.Union(BinaryElementErrors.Keys))
            {
                var elementErrors = new JArray();
                if (ElementErrors.ContainsKey(templateCode))
                {
                    foreach (var error in ElementErrors[templateCode])
                    {
                        elementErrors.Add(error.SerializeToJson());
                    }
                }

                if (BinaryElementErrors.ContainsKey(templateCode))
                {
                    foreach (var error in BinaryElementErrors[templateCode])
                    {
                        elementErrors.Add(error.SerializeToJson());
                    }
                }

                elements.Add(new JObject
                    {
                        { Tokens.TemplateCodeToken, templateCode },
                        { Tokens.ErrorsToken, elementErrors }
                    });
            }

            return new JObject
                {
                    { Tokens.ErrorsToken, new JArray() },
                    { Tokens.ElementsToken, elements }
                };
        }
    }
}