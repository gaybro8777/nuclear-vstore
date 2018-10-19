using System.Collections.Generic;

namespace CloningTool.Json
{
    public class ModerationResult
    {
        public ModerationStatus Status { get; set; }

        public ModerationStatus Resolution => Status;

        public List<ModerationRemark> Remarks { get; set; }

        public bool ShouldSerializeStatus() => false;
    }
}
