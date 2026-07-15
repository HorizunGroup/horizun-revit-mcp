using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class KeiSqlGuardTests
    {
        [Theory]
        [InlineData("INSERT INTO ProjectEquipmentTypes (ProjectTypeName) VALUES ('x')")]
        [InlineData("UPDATE ProjectEquipmentTypes SET Brand = 'A' WHERE ProjectTypeId = 1")]
        [InlineData("UPDATE ProjectEquipmentTypes SET CreatedAt = '2026-01-01' WHERE ProjectTypeId = 1")]
        [InlineData("DELETE FROM TypedSpecs WHERE ProjectTypeId = 1")]
        [InlineData("REPLACE INTO ProjectEquipments (Tag) VALUES ('P-01')")]
        [InlineData("WITH t AS (SELECT 1 AS id) INSERT INTO ProjectEquipments (Tag) SELECT 'x'")]
        public void Allows_dml(string sql)
        {
            Assert.Null(KeiSqlGuard.ValidateWriteStatement(sql));
        }

        [Theory]
        [InlineData("SELECT * FROM ProjectEquipmentTypes")]
        [InlineData("DROP TABLE ProjectEquipmentTypes")]
        [InlineData("ALTER TABLE ProjectEquipmentTypes ADD COLUMN x TEXT")]
        [InlineData("CREATE TABLE foo (id INTEGER)")]
        [InlineData("PRAGMA journal_mode=WAL")]
        [InlineData("ATTACH DATABASE 'x.db' AS other")]
        [InlineData("WITH t AS (SELECT 1) SELECT * FROM t")]
        [InlineData("UPDATE ProjectEquipmentTypes SET Brand='A'; DELETE FROM ProjectEquipments")]
        public void Blocks_non_dml_or_unsafe(string sql)
        {
            Assert.NotNull(KeiSqlGuard.ValidateWriteStatement(sql));
        }

        [Fact]
        public void NormalizeStatements_accepts_batch()
        {
            var list = KeiSqlGuard.NormalizeStatements(
                new[]
                {
                    "UPDATE ProjectEquipmentTypes SET Brand='A' WHERE ProjectTypeId=1",
                    "INSERT INTO TypedSpecs (ProjectTypeId, ParameterCode, Value) VALUES (1,'FlowRate',10)"
                },
                out var error);

            Assert.Null(error);
            Assert.Equal(2, list.Count);
        }
    }
}
