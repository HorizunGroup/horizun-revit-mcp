// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// L10: the cut of a large requirement set, wired to a tool. horizun_plan_reinforcement,
// horizun_apply_reinforcement and horizun_audit_reinforcement take
// `chunk: { by, hosts_per_piece, piece_index, partition_sha256 }` and run ONE piece of
// the set; every piece of one loop must be a piece of the SAME partition, which the
// partition digest proves. The host inventory below has the shape the command reads
// off the model (element id, BuiltInCategory name, type name, Mark, level name).
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RequirementSetPieceTests
    {
        private static JObject Set() => JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'cartilla', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' } ],
  'section_rules': [
    { 'id': 'V1', 'host': { 'mark': 'V-1' }, 'cover': { 'distance_mm': 40 },
      'layers': [ { 'name': 'top', 'face': 'top', 'count': 2, 'bar_type': 'N5', 'extend': 'host_length' } ] },
    { 'id': 'V2', 'host': { 'category': 'OST_StructuralFraming', 'type_name': 'B 300x500' }, 'cover': { 'distance_mm': 40 },
      'layers': [ { 'name': 'bottom', 'face': 'bottom', 'count': 3, 'bar_type': 'N5', 'extend': 'host_length' } ] }
  ] }");

        private static List<RequirementSetChunks.Host> Hosts() => new List<RequirementSetChunks.Host>
        {
            new RequirementSetChunks.Host { ElementId = 11, Category = "OST_StructuralFraming", TypeName = "B 300x500", Mark = "V-1", Level = "L1" },
            new RequirementSetChunks.Host { ElementId = 12, Category = "OST_StructuralFraming", TypeName = "B 300x500", Mark = "V-1", Level = "L1" },
            new RequirementSetChunks.Host { ElementId = 13, Category = "OST_StructuralFraming", TypeName = "B 300x600", Mark = "V-1", Level = "L1" },
            new RequirementSetChunks.Host { ElementId = 21, Category = "OST_StructuralFraming", TypeName = "B 300x500", Mark = "V-2", Level = "L2" },
            new RequirementSetChunks.Host { ElementId = 22, Category = "OST_StructuralFraming", TypeName = "B 300x600", Mark = "V-1", Level = "L2" }
        };

        private static RequirementSetChunks.Request Read(string json, out string code)
        {
            string error;
            RequirementSetChunks.Request r = RequirementSetChunks.ReadRequest(JToken.Parse(json), out code, out error);
            if (r == null) Assert.False(string.IsNullOrWhiteSpace(error));
            return r;
        }

        [Fact]
        public void A_chunk_request_reads_its_four_keys()
        {
            RequirementSetChunks.Request r = Read("{ 'by': 'level', 'hosts_per_piece': 2, 'piece_index': 1 }", out string code);
            Assert.NotNull(r);
            Assert.Null(code);
            Assert.Equal(RequirementSetChunks.ByLevel, r.By);
            Assert.Equal(2, r.HostsPerPiece);
            Assert.Equal(1, r.PieceIndex);
            Assert.Null(r.PartitionSha256);
        }

        [Theory]
        [InlineData("'level'")]                                                                // not an object
        [InlineData("{ 'by': 'level', 'hosts_per_piece': 2 }")]                                 // piece_index missing
        [InlineData("{ 'by': 'level', 'piece_index': 0 }")]                                     // hosts_per_piece missing
        [InlineData("{ 'hosts_per_piece': 2, 'piece_index': 0 }")]                              // by missing
        [InlineData("{ 'by': 'floor', 'hosts_per_piece': 2, 'piece_index': 0 }")]               // not in the vocabulary
        [InlineData("{ 'by': 'level', 'hosts_per_piece': 0, 'piece_index': 0 }")]               // below one
        [InlineData("{ 'by': 'level', 'hosts_per_piece': 2001, 'piece_index': 0 }")]            // above the host_ids ceiling
        [InlineData("{ 'by': 'level', 'hosts_per_piece': 2.5, 'piece_index': 0 }")]             // not a whole number
        [InlineData("{ 'by': 'level', 'hosts_per_piece': '2', 'piece_index': 0 }")]             // a string is not a number
        [InlineData("{ 'by': 'level', 'hosts_per_piece': 2, 'piece_index': -1 }")]              // negative
        [InlineData("{ 'by': 'level', 'hosts_per_piece': 2, 'piece_index': 0, 'size': 3 }")]    // an unknown key
        [InlineData("{ 'by': 'level', 'hosts_per_piece': 2, 'piece_index': 0, 'partition_sha256': 'abc' }")]
        public void A_chunk_request_that_is_not_exactly_right_is_refused(string json)
        {
            RequirementSetChunks.Request r = Read(json, out string code);
            Assert.Null(r);
            Assert.Equal(RequirementSetChunks.CodeBadRequest, code);
        }

        [Fact]
        public void Selecting_a_piece_returns_that_piece_and_names_the_partition()
        {
            RequirementSetChunks.Request r = Read("{ 'by': 'level', 'hosts_per_piece': 2, 'piece_index': 1 }", out _);
            RequirementSetChunks.Selection s = RequirementSetChunks.Select(Set(), Hosts(), r, false);
            Assert.True(s.Ok, s.Why);
            Assert.Equal(3, s.PieceCount);                                  // L1: 11,12 | 13 ; L2: 21,22
            Assert.Equal(1, s.Piece.Index);
            Assert.Equal("L1", s.Piece.Key);
            Assert.Equal(new long[] { 13 }, s.Piece.ElementIds.ToArray());
            Assert.Matches("^[0-9a-f]{64}$", s.PartitionSha256);
            StructuralRequirementSet piece = StructuralRequirementSet.Load(s.Piece.RequirementSet);
            Assert.True(piece.Ok, piece.Error);
            Assert.Equal(new[] { "V1" }, piece.SectionRules.Select(x => x.Id).ToArray());

            JObject j = s.ToJson();
            Assert.Equal(1, (int)j["piece_index"]);
            Assert.Equal(3, (int)j["piece_count"]);
            Assert.Equal(2, (int)j["next_piece_index"]);
            Assert.Equal(s.PartitionSha256, (string)j["partition_sha256"]);
            Assert.Equal(StructuralRequirementSet.Sha256Of(Set()), (string)j["requirement_set_sha256"]);
            Assert.Equal(StructuralRequirementSet.Sha256Of(s.Piece.RequirementSet), (string)j["piece"]["requirement_set_sha256"]);
            Assert.Equal(new long[] { 13 }, j["piece"]["element_ids"].Select(x => (long)x).ToArray());
            Assert.Equal(new[] { 2, 1, 2 }, j["pieces"].Select(p => (int)p["hosts"]).ToArray());
            Assert.Equal(5, (int)j["hosts_covered"]);
        }

        [Fact]
        public void The_last_piece_says_there_is_no_next_one()
        {
            RequirementSetChunks.Request r = Read("{ 'by': 'level', 'hosts_per_piece': 2, 'piece_index': 2 }", out _);
            JObject j = RequirementSetChunks.Select(Set(), Hosts(), r, false).ToJson();
            Assert.Equal(JTokenType.Null, j["next_piece_index"].Type);
        }

        [Fact]
        public void The_partition_digest_is_stable_and_moves_with_anything_that_changes_the_cut()
        {
            RequirementSetChunks.Request r0 = Read("{ 'by': 'level', 'hosts_per_piece': 2, 'piece_index': 0 }", out _);
            RequirementSetChunks.Request r2 = Read("{ 'by': 'level', 'hosts_per_piece': 2, 'piece_index': 2 }", out _);
            string a = RequirementSetChunks.Select(Set(), Hosts(), r0, false).PartitionSha256;
            // Another piece of the same partition: the same digest.
            Assert.Equal(a, RequirementSetChunks.Select(Set(), Hosts(), r2, false).PartitionSha256);
            // Another size, another dimension, another host, another set: another partition.
            RequirementSetChunks.Request size3 = Read("{ 'by': 'level', 'hosts_per_piece': 3, 'piece_index': 0 }", out _);
            Assert.NotEqual(a, RequirementSetChunks.Select(Set(), Hosts(), size3, false).PartitionSha256);
            RequirementSetChunks.Request byType = Read("{ 'by': 'host_type', 'hosts_per_piece': 2, 'piece_index': 0 }", out _);
            Assert.NotEqual(a, RequirementSetChunks.Select(Set(), Hosts(), byType, false).PartitionSha256);
            List<RequirementSetChunks.Host> more = Hosts();
            more.Add(new RequirementSetChunks.Host { ElementId = 14, Category = "OST_StructuralFraming", TypeName = "B 300x500", Mark = "V-1", Level = "L1" });
            Assert.NotEqual(a, RequirementSetChunks.Select(Set(), more, r0, false).PartitionSha256);
            JObject other = Set();
            other["requirement_set"]["version"] = "2";
            Assert.NotEqual(a, RequirementSetChunks.Select(other, Hosts(), r0, false).PartitionSha256);
        }

        [Fact]
        public void A_piece_of_another_partition_is_refused()
        {
            RequirementSetChunks.Request r = Read("{ 'by': 'level', 'hosts_per_piece': 2, 'piece_index': 0, 'partition_sha256': '" +
                                                  new string('a', 64) + "' }", out _);
            RequirementSetChunks.Selection s = RequirementSetChunks.Select(Set(), Hosts(), r, true);
            Assert.False(s.Ok);
            Assert.Equal(RequirementSetChunks.CodePartitionChanged, s.Code);
            Assert.Null(s.Piece);
        }

        [Fact]
        public void The_matching_digest_is_accepted()
        {
            RequirementSetChunks.Request r = Read("{ 'by': 'level', 'hosts_per_piece': 2, 'piece_index': 0 }", out _);
            string sha = RequirementSetChunks.Select(Set(), Hosts(), r, false).PartitionSha256;
            RequirementSetChunks.Request again = Read("{ 'by': 'level', 'hosts_per_piece': 2, 'piece_index': 1, 'partition_sha256': '" + sha + "' }", out _);
            RequirementSetChunks.Selection s = RequirementSetChunks.Select(Set(), Hosts(), again, true);
            Assert.True(s.Ok, s.Why);
            Assert.Equal(1, s.Piece.Index);
        }

        [Fact]
        public void A_write_without_the_partition_digest_is_refused_and_not_told_the_digest()
        {
            RequirementSetChunks.Request r = Read("{ 'by': 'level', 'hosts_per_piece': 2, 'piece_index': 0 }", out _);
            RequirementSetChunks.Selection s = RequirementSetChunks.Select(Set(), Hosts(), r, true);
            Assert.False(s.Ok);
            Assert.Equal(RequirementSetChunks.CodePartitionRequired, s.Code);
            // Handing the digest back in the refusal would let a caller copy it without ever reading the partition.
            Assert.Null(s.PartitionSha256);
            Assert.DoesNotContain("partition_sha256\":\"", s.ToJson().ToString(Newtonsoft.Json.Formatting.None).Replace(" ", ""));
        }

        [Fact]
        public void A_piece_past_the_last_is_refused_with_the_count()
        {
            RequirementSetChunks.Request r = Read("{ 'by': 'level', 'hosts_per_piece': 2, 'piece_index': 3 }", out _);
            RequirementSetChunks.Selection s = RequirementSetChunks.Select(Set(), Hosts(), r, false);
            Assert.False(s.Ok);
            Assert.Equal(RequirementSetChunks.CodePieceOutOfRange, s.Code);
            Assert.Equal(3, s.PieceCount);
            Assert.Contains("3", s.Why);
        }

        [Fact]
        public void A_split_refusal_is_passed_through_with_its_own_code()
        {
            RequirementSetChunks.Request r = Read("{ 'by': 'level', 'hosts_per_piece': 2, 'piece_index': 0 }", out _);
            List<RequirementSetChunks.Host> hosts = Hosts();
            hosts[0].Level = null;
            RequirementSetChunks.Selection s = RequirementSetChunks.Select(Set(), hosts, r, false);
            Assert.False(s.Ok);
            Assert.Equal(RequirementSetChunks.CodeMissingKey, s.Code);
        }
    }
}
