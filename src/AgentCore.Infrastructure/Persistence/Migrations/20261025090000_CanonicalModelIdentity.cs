using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations
{
    // Refuses affected installed references; no aliases are converted or discarded.
    public partial class CanonicalModelIdentity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(FrozenMigrationMetadata.Read("f8c850bbe0e7e2c233d512827b924263a0903f5eeac2d11cc796b5ad68e88738"));
            migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
            migrationBuilder.Sql(FrozenMigrationMetadata.Read("c56ce267368ce3b576c1e80b1537d3991cc8cd5471dbcf146929adb23df19bf3"));
            migrationBuilder.RenameColumn(name: "ModelCatalogKey", table: "TriggerOccurrences", newName: "ModelOfferingId");
            migrationBuilder.RenameColumn(name: "ModelProviderAlias", table: "TriggerOccurrences", newName: "ModelConnectionId");
            migrationBuilder.RenameColumn(name: "ModelId", table: "TriggerOccurrences", newName: "ExternalModelId");
            migrationBuilder.RenameColumn(name: "ModelCatalogKey", table: "SessionSnapshots", newName: "ModelOfferingId");
            migrationBuilder.RenameColumn(name: "ModelProviderAlias", table: "SessionSnapshots", newName: "ModelConnectionId");
            migrationBuilder.RenameColumn(name: "ModelId", table: "SessionSnapshots", newName: "ExternalModelId");
            migrationBuilder.RenameColumn(name: "ModelCatalogKey", table: "ConversationEntries", newName: "ModelOfferingId");
            migrationBuilder.RenameColumn(name: "ModelProviderAlias", table: "ConversationEntries", newName: "ModelConnectionId");
            migrationBuilder.RenameColumn(name: "ModelId", table: "ConversationEntries", newName: "ExternalModelId");
            migrationBuilder.RenameColumn(name: "SummaryModelCatalogKey", table: "SessionSnapshots", newName: "SummaryModelOfferingId");
            migrationBuilder.RenameColumn(name: "SummaryModelProviderAlias", table: "SessionSnapshots", newName: "SummaryModelConnectionId");
            migrationBuilder.RenameColumn(name: "SummaryModelId", table: "SessionSnapshots", newName: "SummaryExternalModelId");
            migrationBuilder.RenameColumn(name: "UnattendedModelCatalogKey", table: "AgentInstances", newName: "UnattendedOfferingId");
            migrationBuilder.RenameColumn(name: "ModelOverrideCatalogKey", table: "Automations", newName: "ModelOverrideOfferingId");
            migrationBuilder.Sql(FrozenMigrationMetadata.Read("ca9821ddd042308bcbd5add0dded1a7789703c51069d82c9a6ebc52f5d5e07c4"));
            migrationBuilder.Sql("CREATE TEMP TABLE CanonicalModelForeignKeys(Rows INTEGER CHECK(Rows=0)); INSERT INTO CanonicalModelForeignKeys SELECT count(*) FROM pragma_foreign_key_check; DROP TABLE CanonicalModelForeignKeys;");
            migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(FrozenMigrationMetadata.Read("1d67eaa7340220dcb7c698569be5dc995984f5fa4da438fd77d265e7dc2e1e1e"));
            migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
            migrationBuilder.Sql(FrozenMigrationMetadata.Read("c56ce267368ce3b576c1e80b1537d3991cc8cd5471dbcf146929adb23df19bf3"));
            migrationBuilder.RenameColumn(name: "ModelOverrideOfferingId", table: "Automations", newName: "ModelOverrideCatalogKey");
            migrationBuilder.RenameColumn(name: "UnattendedOfferingId", table: "AgentInstances", newName: "UnattendedModelCatalogKey");
            migrationBuilder.RenameColumn(name: "SummaryExternalModelId", table: "SessionSnapshots", newName: "SummaryModelId");
            migrationBuilder.RenameColumn(name: "SummaryModelConnectionId", table: "SessionSnapshots", newName: "SummaryModelProviderAlias");
            migrationBuilder.RenameColumn(name: "SummaryModelOfferingId", table: "SessionSnapshots", newName: "SummaryModelCatalogKey");
            migrationBuilder.RenameColumn(name: "ExternalModelId", table: "ConversationEntries", newName: "ModelId");
            migrationBuilder.RenameColumn(name: "ModelConnectionId", table: "ConversationEntries", newName: "ModelProviderAlias");
            migrationBuilder.RenameColumn(name: "ModelOfferingId", table: "ConversationEntries", newName: "ModelCatalogKey");
            migrationBuilder.RenameColumn(name: "ExternalModelId", table: "SessionSnapshots", newName: "ModelId");
            migrationBuilder.RenameColumn(name: "ModelConnectionId", table: "SessionSnapshots", newName: "ModelProviderAlias");
            migrationBuilder.RenameColumn(name: "ModelOfferingId", table: "SessionSnapshots", newName: "ModelCatalogKey");
            migrationBuilder.RenameColumn(name: "ExternalModelId", table: "TriggerOccurrences", newName: "ModelId");
            migrationBuilder.RenameColumn(name: "ModelConnectionId", table: "TriggerOccurrences", newName: "ModelProviderAlias");
            migrationBuilder.RenameColumn(name: "ModelOfferingId", table: "TriggerOccurrences", newName: "ModelCatalogKey");
            migrationBuilder.Sql(FrozenMigrationMetadata.Read("e4e19b16eec82414c29e82afed2a012c99091f6e886b88f4dcb74112fae8fa2b"));
            migrationBuilder.Sql("CREATE TEMP TABLE CanonicalModelForeignKeys(Rows INTEGER CHECK(Rows=0)); INSERT INTO CanonicalModelForeignKeys SELECT count(*) FROM pragma_foreign_key_check; DROP TABLE CanonicalModelForeignKeys;");
            migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
        }
    }
}
