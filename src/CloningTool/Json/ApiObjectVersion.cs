using System.Collections.Generic;

namespace CloningTool.Json
{
    public class ApiObjectVersion
    {
        public string Version { get; set; }
        public int VersionIndex { get; set; }
        public ModerationResult Moderation { get; set; }
        public IReadOnlyCollection<ApiObjectVersionModifiedItem> ModifiedItems { get; set; }
    }
}