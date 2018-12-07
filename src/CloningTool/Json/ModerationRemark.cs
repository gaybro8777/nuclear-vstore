namespace CloningTool.Json
{
    public class ModerationRemark
    {
        public string Comment { get; set; }
        public long? RemarkId { get; set; }
        public int? TemplateCode { get; set; }

        public bool ShouldSerializeRemarkId() => RemarkId.HasValue;

        public bool ShouldSerializeTemplateCode() => TemplateCode.HasValue;
    }
}