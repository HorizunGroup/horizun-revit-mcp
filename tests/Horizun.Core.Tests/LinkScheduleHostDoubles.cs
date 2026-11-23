using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Autodesk.Revit.DB
{
    public enum ViewDuplicateOption { Duplicate }

    public partial class Document
    {
        public bool TestScheduleHost;
        public Func<string, Element> TestScheduleUniqueIdLookup;
        public Action<Transaction> TestScheduleBeforeCommit;
        public TransactionStatus TestScheduleCommitStatus = TransactionStatus.Committed;
        public TransactionStatus TestScheduleRollbackStatus = TransactionStatus.RolledBack;
    }

    public partial class Element
    {
        public bool TestScheduleOwned;
        public ElementId GroupId => ElementId.InvalidElementId;
        public List<Parameter> Parameters { get; } = new List<Parameter>();
        public IList<Parameter> GetParameters(string name) => Parameters.Where(p => p.Name == name).ToList();
        public Parameter LookupParameter(string name) => GetParameters(name).FirstOrDefault();
        public Parameter get_Parameter(Guid guid) => Parameters.SingleOrDefault(p => p.GUID == guid);
    }

    public partial class Parameter
    {
        public string Name;
        public string Text;
        public Guid GUID;
        public bool IsShared => GUID != Guid.Empty;
        public ElementId Id { get; set; }
        public bool TestScheduleReadThrows;
        public string AsString() => TestScheduleReadThrows ? throw new InvalidOperationException("parameter read failure") : Text;
        public int TestSetCalls { get; private set; }
        public bool Set(string value)
        {
            TestSetCalls++;
            if (string.Equals(Text, value, StringComparison.Ordinal)) return false;
            Text = value;
            return true;
        }
    }

    public partial class View
    {
        public Exception TestScheduleOverrideReadError;
        public bool CanViewBeDuplicated(ViewDuplicateOption option) => true;
        public ElementId Duplicate(ViewDuplicateOption option)
        {
            var copy = new View { Document = Document, Id = new ElementId(Document.NextElementId++), ViewTemplateId = ViewTemplateId };
            Document.Elements.Add(copy.Id.Value, copy);
            return copy.Id;
        }
    }
}

namespace Horizun.Revit.Commands
{
    internal static class CodeCheckCommand
    {
        public static Category ResolveCategory(Document doc, string category) => doc.Elements.Values
            .Select(e => e.Category).FirstOrDefault(c => CategoryToken(c) == category);
        public static string CategoryToken(Category category) => ((BuiltInCategory)category.Id.Value).ToString();
        public static string SafeCatName(Category category) => category.Name;
        public static string LevelName(Document doc, Element element) => "N1";
        public static ParamFact ReadParam(Parameter parameter, string unit) =>
            new ParamFact { Exists = true, Text = parameter.AsString() };
    }
}

namespace Horizun.Revit.Core
{
}
