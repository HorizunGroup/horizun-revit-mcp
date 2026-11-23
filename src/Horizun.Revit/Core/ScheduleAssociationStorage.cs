using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace Horizun.Revit.Core
{
    public static class ScheduleAssociationStorage
    {
        private static readonly Guid Id = new Guid("cc1a3d86-96e3-4be3-a094-6e606af67be7");
        private static Schema GetSchema()
        {
            Schema schema = Schema.Lookup(Id);
            if (schema != null) return schema;
            var b = new SchemaBuilder(Id);
            b.SetSchemaName("HorizunScheduleNoteAssociationV1");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Vendor);
            b.SetVendorId(HorizunVendor.Id);
            b.AddSimpleField("Json", typeof(string));
            return b.Finish();
        }
        public static void Write(Element note, ScheduleAssociation.Record record)
        {
            if (note.UniqueId != record.NoteUniqueId) throw new ArgumentException("Association note identity differs.");
            var schema = GetSchema(); var entity = new Entity(schema);
            entity.Set<string>(schema.GetField("Json"), ScheduleAssociation.Serialize(record));
            note.SetEntity(entity);
        }
        public static ScheduleAssociation.Record Read(Element note)
        {
            try
            {
                Schema schema = Schema.Lookup(Id);
                if (schema == null || note == null) return null;
                Entity entity = note.GetEntity(schema);
                return entity != null && entity.IsValid() ? ScheduleAssociation.Parse(entity.Get<string>(schema.GetField("Json")), note.UniqueId) : null;
            }
            catch { return null; }
        }
    }
}
