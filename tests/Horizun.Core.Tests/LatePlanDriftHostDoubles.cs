using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Autodesk.Revit.DB
{
    public partial class Document
    {
        internal int TransactionStarts;
        internal JArray CreatedPlacements;
        public ProjectLocation ActiveProjectLocation => null;
        public FixtureSettings Settings { get; } = new FixtureSettings();
        public BindingMap ParameterBindings { get; } = new BindingMap();
    }

    public sealed partial class Wall : Element
    {
        public bool Flipped => throw new NotSupportedException();
    }
    public sealed class ProjectLocation
    {
        public ProjectPosition GetProjectPosition(XYZ point) => throw new NotSupportedException();
    }
    public sealed class ProjectPosition
    {
        public double Angle, EastWest, NorthSouth, Elevation;
    }

    public sealed class FixtureSettings
    {
        public Categories Categories { get; } = new Categories();
    }
    public partial class Category
    {
        public static Category GetCategory(Document doc, BuiltInCategory category) =>
            doc.Settings.Categories.SingleOrDefault(c => c.Id.Value == (long)category);
    }
    public sealed class ForgeTypeId
    {
        public ForgeTypeId(string typeId) { TypeId = typeId; }
        public string TypeId { get; }
        public static bool operator ==(ForgeTypeId a, string b) => a?.TypeId == b;
        public static bool operator !=(ForgeTypeId a, string b) => !(a == b);
        public override bool Equals(object o) => o is ForgeTypeId f && f.TypeId == TypeId;
        public override int GetHashCode() => TypeId?.GetHashCode() ?? 0;
    }
    public static class GroupTypeId
    {
        public static ForgeTypeId IdentityData { get; } = new ForgeTypeId("fixture:group.identity");
    }
    public class Definition
    {
        public string Name { get; set; }
        public string DataType { get; set; } = SpecTypeId.Length;
        public ForgeTypeId GetDataType() => new ForgeTypeId(DataType);
    }
    public sealed class ExternalDefinition : Definition { public Guid GUID { get; set; } }
    public sealed class InternalDefinition : Definition
    {
        public ElementId Id { get; set; }
        public BuiltInParameter BuiltInParameter { get; set; } = BuiltInParameter.INVALID;
        public bool VariesAcrossGroups { get; private set; }
        public void SetAllowVaryBetweenGroups(Document doc, bool value) => VariesAcrossGroups = value;
    }
    public sealed class SharedParameterElement
    {
        private readonly InternalDefinition definition;
        private SharedParameterElement(InternalDefinition definition) { this.definition = definition; }
        public InternalDefinition GetDefinition() => definition;
        public static SharedParameterElement Lookup(Document doc, Guid guid) =>
            doc.ParameterBindings.Definitions.TryGetValue(guid, out var definition) ? new SharedParameterElement(definition) : null;
    }
    public sealed class DefinitionFile { public List<DefinitionGroup> Groups { get; } = new List<DefinitionGroup>(); }
    public sealed class DefinitionGroup { public List<Definition> Definitions { get; } = new List<Definition>(); }
    public sealed class CategorySet : HashSet<Category> { public bool Insert(Category cat) => Add(cat); }
    public class Binding { }
    public class ElementBinding : Binding { public CategorySet Categories { get; set; } }
    public sealed class InstanceBinding : ElementBinding { }
    public sealed class TypeBinding : ElementBinding { }
    public sealed class BindingMap
    {
        private readonly Dictionary<Definition, Binding> bindings = new Dictionary<Definition, Binding>();
        internal readonly Dictionary<Guid, InternalDefinition> Definitions = new Dictionary<Guid, InternalDefinition>();
        internal int Inserts, Reinserts;

        public bool Insert(Definition key, Binding binding, ForgeTypeId group)
        {
            Inserts++;
            var external = (ExternalDefinition)key;
            var stored = new InternalDefinition
            {
                Id = new ElementId(Definitions.Count + 1),
                Name = external.Name,
                DataType = external.DataType
            };
            Definitions.Add(external.GUID, stored);
            bindings.Add(stored, binding);
            return true;
        }

        public bool ReInsert(Definition key, Binding binding, ForgeTypeId group)
        {
            Reinserts++;
            if (!bindings.ContainsKey(key)) return false;
            bindings[key] = binding;
            return true;
        }

        public DefinitionBindingMapIterator ForwardIterator() => new DefinitionBindingMapIterator(bindings.ToArray());
    }
    public sealed class DefinitionBindingMapIterator
    {
        private readonly KeyValuePair<Definition, Binding>[] bindings;
        private int index;
        internal DefinitionBindingMapIterator(KeyValuePair<Definition, Binding>[] bindings) { this.bindings = bindings; }
        public Definition Key => bindings[index].Key;
        public object Current => bindings[index].Value;
        public void Reset() => index = -1;
        public bool MoveNext() => ++index < bindings.Length;
    }
    public interface IFailuresPreprocessor { FailureProcessingResult PreprocessFailures(FailuresAccessor accessor); }
    public enum FailureProcessingResult { Continue }
    public enum FailureSeverity { Warning }
    public sealed class FailureMessageAccessor { public FailureSeverity GetSeverity() => FailureSeverity.Warning; }
    public sealed class FailuresAccessor
    {
        public IEnumerable<FailureMessageAccessor> GetFailureMessages() => Array.Empty<FailureMessageAccessor>();
        public void DeleteWarning(FailureMessageAccessor warning) { }
    }
}

namespace Autodesk.Revit.UI
{
    public partial class TestApplication
    {
        public string SharedParametersFilename { get; set; }
        internal Func<string, DefinitionFile> ReadFixtureSharedParameters;
        public DefinitionFile OpenSharedParameterFile() => ReadFixtureSharedParameters(SharedParametersFilename);
        public FixtureBindingFactory Create { get; } = new FixtureBindingFactory();
    }

    public sealed class FixtureBindingFactory
    {
        public CategorySet NewCategorySet() => new CategorySet();
        public TypeBinding NewTypeBinding(CategorySet categories) => new TypeBinding { Categories = categories };
        public InstanceBinding NewInstanceBinding(CategorySet categories) => new InstanceBinding { Categories = categories };
    }
}

namespace Horizun.Revit.Core
{
}
