﻿namespace MigrationTool.Json
{
    public class PositionDescriptor
    {
        public long Id { get; set; }

        public string Name { get; set; }

        public bool IsDeleted { get; set; }

        public bool IsContentSales { get; set; }

        public TemplateDescriptor Template { get; set; }

        public class TemplateDescriptor
        {
            public long Id { get; set; }

            public string Name { get; set; }
        }
    }
}
