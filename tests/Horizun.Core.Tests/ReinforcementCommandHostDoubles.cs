// Supply host measurements and transaction outcomes to the production command entry points.
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Autodesk.Revit.DB
{
    public partial class Document
    {
        internal SectionHostFrame TestReinforcementFrame { get; set; }
        internal bool TestReinforcementPartial { get; set; }
        internal bool TestReinforcementCommitted { get; set; }
        internal TransactionStatus TestCommitStatus { get; set; } = TransactionStatus.Committed;
        internal TransactionStatus? TestStatusAfterCommit { get; set; }
        internal Transaction TestPendingTransaction { get; set; }
        internal Exception TestTransactionStartError { get; set; }
        internal Exception TestPendingTransactionDisposeError { get; set; }
        internal CommandResult TestReinforcementGateRefusal { get; set; }
        internal List<ResolvedCoverRow> TestReinforcementCovers { get; } = new List<ResolvedCoverRow>();
        internal List<ResolvedSystemRow> TestReinforcementSystems { get; } = new List<ResolvedSystemRow>();
    }

    internal sealed class ReinforcementBar : Element
    {
        // The Core ElementId double compares by reference.
        internal static readonly ElementId BarTypeId = new ElementId(1);
        internal Document TestDocument { get; set; }
        internal bool FailTypeReadAfterCommit { get; set; }
        internal int PostCommitTypeReads { get; private set; }

        public override ElementId GetTypeId()
        {
            if (TestDocument.TestReinforcementCommitted)
            {
                PostCommitTypeReads++;
                if (FailTypeReadAfterCommit)
                    throw new InvalidOperationException("Bar type cannot be read after commit.");
            }
            return BarTypeId;
        }
    }
}

namespace Autodesk.Revit.DB.Structure
{
    public enum YJustification { Left, Center, Origin, Right }
    public enum YZJustificationOption { Uniform, Independent }

    public class RebarHookType : Element
    {
        internal Document FailIdReadAfterCommitIn { get; set; }

        public override ElementId Id
        {
            get
            {
                if (FailIdReadAfterCommitIn?.TestReinforcementCommitted == true)
                    throw new InvalidOperationException("Hook type id cannot be read after commit.");
                return base.Id;
            }
            set => base.Id = value;
        }
    }

    public class RebarBarType : Element
    {
        internal Document TestDocument { get; set; }
        internal double LengthFeet { get; set; }
        internal bool FailReadAfterCommit { get; set; }
        internal int LengthWrites { get; private set; }
        internal int PostCommitLengthReads { get; private set; }
        private bool _auto;

        public bool GetAutoCalcHookLengths(ElementId hookId) => _auto;
        public void SetAutoCalcHookLengths(ElementId hookId, bool value) => _auto = value;
        public double GetHookLength(ElementId hookId)
        {
            if (TestDocument.TestReinforcementCommitted)
            {
                PostCommitLengthReads++;
                if (FailReadAfterCommit) throw new InvalidOperationException("Hook length cannot be read after commit.");
            }
            return LengthFeet;
        }
        public void SetHookLength(ElementId hookId, double value)
        {
            LengthWrites++;
            LengthFeet = value;
        }
    }
}

namespace Horizun.Revit.Commands
{

    public static partial class ReinforcementResolver
    {
        public const string CodeBarTypeAmbiguous = "bar_type_ambiguous";
        public const string CodeBarTypeNotFound = "bar_type_not_found";
        public const string CodeHookAmbiguous = "hook_type_ambiguous";
        public const string CodeHookNotFound = "hook_type_not_found";

        public static T ByName<T>(Document doc, string name, out int matches) where T : Element
        {
            var found = doc.Elements.Values.OfType<T>().Where(type => type.Name == name).ToList();
            matches = found.Count;
            return matches == 1 ? found[0] : null;
        }

        public static void ForgetMeshes() { }
        public static IReadOnlyList<string> AllCodes => Array.Empty<string>();
        
        
        
        
        public static JObject DescribeCoverRow(ResolvedCoverRow row, int index) => new JObject();

        

        
    }

    public sealed class ResolvedRebarRow
    {
        public StructuralRebarRule Rule { get; set; }
        public Element Host { get; set; }
        public bool Ok => true;
        public string Code => null;
        public string Why => null;
        public JObject SectionWarning { get; set; }
        public RebarLayoutPlan Layout => RebarLayoutRules.Resolve(Rule.Layout);
        public double ExpectedBarLengthMm => 5904;
    }

    public sealed class ResolvedCoverRow
    {
        public long HostId { get; set; }
        public bool Applied { get; set; }
        public bool FailRead { get; set; }
        
        public bool Ok => true;
        public bool AlreadyRight => false;
        public string Code => throw new NotSupportedException();
        public string Why => throw new NotSupportedException();
    }

    internal static class ReinforcementHostInput
    {
        public static JObject Validate(Document doc, List<long> ids) => null;
    }

    internal sealed class ChunkOutcome
    {
        public bool Ok => throw new NotSupportedException();
        public CommandResult Refusal => throw new NotSupportedException();
        
        public JObject Chunking => throw new NotSupportedException();
    }

    internal static class SystemReinforcementResolver
    {
        public static IReadOnlyList<string> AllCodes => Array.Empty<string>();
        
        
    }

    internal sealed class ResolvedSystemRow
    {
        public long ElementId { get; set; }
        public bool FailRead { get; set; }
        
        public string RuleId => throw new NotSupportedException();
        public bool Ok => true;
        public bool Required => throw new NotSupportedException();
        public string Code => throw new NotSupportedException();
        public string Why => throw new NotSupportedException();
    }

}

namespace Horizun.Revit.Core
{
    public sealed class SilentRollbackException : Exception
    {
        public TransactionStatus Status { get; }
        public SilentRollbackException(string message) : base(message) { Status = TransactionStatus.Pending; }
        public SilentRollbackException(TransactionStatus status)
            : base("Revit returned " + status + ". Nothing was written.")
        {
            Status = status;
        }
    }
}
