// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A provenance row is `written` only after its transaction committed and the
// record read back off the element (audit 2026-10-08, 6.2).
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ProvenanceLedgerTests
    {
        [Fact]
        public void A_row_is_not_written_while_its_transaction_is_open()
        {
            var ledger = new ProvenanceLedger();
            var row = new JObject { ["element_id"] = 7, ["written"] = true };
            ledger.Wrote(row, () => null);
            Assert.False(row.Value<bool>("written"));
            Assert.Equal("pending_commit", row.Value<string>("written_state"));
            Assert.Equal(1, ledger.PendingCount);
        }

        [Fact]
        public void A_rolled_back_transaction_writes_nothing()
        {
            var ledger = new ProvenanceLedger();
            var a = new JObject(); var b = new JObject();
            bool read = false;
            ledger.Wrote(a, () => { read = true; return null; });
            ledger.Wrote(b, () => null);
            Assert.Equal(0, ledger.Settle(false, "RolledBack"));
            Assert.False(a.Value<bool>("written"));
            Assert.False(b.Value<bool>("written"));
            Assert.Contains("RolledBack", a.Value<string>("means"));
            Assert.False(read);   // a record that was never committed is not read back as if it were
        }

        [Fact]
        public void A_committed_row_is_written_only_when_it_reads_back()
        {
            var ledger = new ProvenanceLedger();
            var good = new JObject(); var lost = new JObject(); var threw = new JObject();
            ledger.Wrote(good, () => null);
            ledger.Wrote(lost, () => "the element carries no CAD provenance");
            ledger.Wrote(threw, () => throw new System.InvalidOperationException("element deleted"));
            Assert.Equal(1, ledger.Settle(true, "Committed"));
            Assert.True(good.Value<bool>("written"));
            Assert.Equal("read_back", good.Value<string>("written_state"));
            Assert.False(lost.Value<bool>("written"));
            Assert.Contains("no CAD provenance", lost.Value<string>("means"));
            Assert.False(threw.Value<bool>("written"));
            Assert.Contains("element deleted", threw.Value<string>("means"));
            Assert.Equal(0, ledger.PendingCount);
        }

        [Fact]
        public void The_cad_comparison_names_the_first_field_that_differs()
        {
            var meant = new CadProvenance { CandidateId = "c1", SemanticId = "s1", RuleId = "r", PlanFingerprint = "p", PlacementId = null };
            var same = new CadProvenance { CandidateId = "c1", SemanticId = "s1", RuleId = "r", PlanFingerprint = "p", PlacementId = "" };
            Assert.Null(ProvenanceLedger.CadDiffers(meant, same, null));
            Assert.Equal("candidate_id differs", ProvenanceLedger.CadDiffers(meant,
                new CadProvenance { CandidateId = "c2", SemanticId = "s1", RuleId = "r", PlanFingerprint = "p" }, null));
            Assert.Equal("the element carries no CAD provenance", ProvenanceLedger.CadDiffers(meant, null, null));
            Assert.Equal("newer schema", ProvenanceLedger.CadDiffers(meant, same, "newer schema"));
        }
    }
}
