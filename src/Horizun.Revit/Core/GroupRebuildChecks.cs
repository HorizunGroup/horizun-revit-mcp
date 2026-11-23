using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal sealed class GroupRebuildChecks
    {
        public bool Committed { get; private set; }
        public PostconditionCheck Check { get; private set; }
        public bool Verified => Committed && Check?.AllVerified == true;

        // Both rehearsal and apply use this path while their TransactionGroup can roll back.
        public void Run(Func<PostconditionCheck> verifyProvisional, Action commit, Func<PostconditionCheck> verifyCommitted)
        {
            Check = verifyProvisional();
            if (!Check.AllVerified) return;
            commit();
            Committed = true;
            Check = verifyCommitted();
        }

        internal sealed class Deletion
        {
            public long Id;
            public string UniqueId;
            public bool Owned, InRegenerationScope;
            /// <summary>Part of the hidden definition of the group type being replaced: it carries no mark of
            /// its own and goes with the type, whose own deletion row is checked for ownership.</summary>
            public bool TypeDefinition;
            public string Category, OwnershipReason;
            public bool Authorized(bool acceptRegeneration) => !string.IsNullOrEmpty(UniqueId) &&
                InRegenerationScope && (Owned || TypeDefinition || acceptRegeneration);

            public JObject Evidence(bool acceptRegeneration) => new JObject
            {
                ["element_id"] = Id, ["unique_id"] = UniqueId,
                ["in_regeneration_scope"] = InRegenerationScope, ["owned"] = Owned, ["type_definition"] = TypeDefinition,
                ["authorized"] = Authorized(acceptRegeneration),
                ["category"] = Category, ["ownership_reason"] = OwnershipReason
            };

            public PlannedElement PlanRow() => new PlannedElement
            {
                ElementId = Id, UniqueId = UniqueId, Action = PlannedAction.Delete,
                BeforeValues = new Dictionary<string, string>
                {
                    ["element_id"] = Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["ownership"] = Owned ? "owned" : TypeDefinition ? "type_definition" : "accepted_regeneration",
                    ["in_regeneration_scope"] = InRegenerationScope ? "true" : "false"
                }
            };
        }

        public static bool DeletionsMatch(IEnumerable<long> observed, IEnumerable<long> required,
            IEnumerable<long> approved, Func<long, bool> exists)
        {
            if (observed == null) return false;
            var ids = new HashSet<long>(observed);
            return ids.IsSupersetOf(required) && (approved == null || ids.SetEquals(approved)) && !ids.Any(exists);
        }

        public static void RecordChanges(PostconditionCheck check, JArray priorAdditions, JArray additions,
            long[] deletions, long[] required, long[] approved, Func<long, bool> exists)
        {
            check.Record("also_created", priorAdditions, additions,
                additions != null && (priorAdditions == null || JToken.DeepEquals(priorAdditions, additions)));
            check.Record("deleted_elements", new JArray(approved ?? required), deletions == null ? null : new JArray(deletions),
                DeletionsMatch(deletions, required, approved, exists));
        }
    }
}
