using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class SourceTraceStorage
    {
        private static readonly Guid Id = new Guid("f9dceab3-7234-49f4-b77c-b701982c90ab");
        private static Schema GetSchema()
        {
            var schema = Schema.Lookup(Id);
            if (schema != null) return schema;
            var builder = new SchemaBuilder(Id);
            builder.SetSchemaName("HorizunSourceReferenceV1");
            builder.SetReadAccessLevel(AccessLevel.Public); builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("Json", typeof(string)); return builder.Finish();
        }
        public static void Write(Element element, JObject trace)
        {
            SourceTrace.Validate(trace);
            var schema = GetSchema(); var entity = new Entity(schema);
            entity.Set<string>(schema.GetField("Json"), trace.ToString(Formatting.None)); element.SetEntity(entity);
        }
        public static JObject Read(Element element)
        {
            var schema = Schema.Lookup(Id);
            if (schema == null) return null;
            var entity = element.GetEntity(schema);
            return entity.IsValid() ? JObject.Parse(entity.Get<string>(schema.GetField("Json"))) : null;
        }
    }
}
