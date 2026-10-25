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
            migrationBuilder.Sql(FrozenMigrationMetadata.Read("aaba7f0481840289b98042a95e13b21e2ceee8808e99282cf8aa89c459008f85"));
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
            migrationBuilder.Sql(FrozenMigrationMetadata.Read("71cda9aec1ebba25dceb07db478757566d2c92c539df630c031d611f10e309ed"));
            migrationBuilder.Sql(FrozenMigrationMetadata.Read("2ac64a630e5520cf70b1ca0a954abc6dbb6807d19a6f4c046a0914083e40fc47"));
            migrationBuilder.Sql("CREATE TEMP TABLE CanonicalModelForeignKeys(Rows INTEGER CHECK(Rows=0)); INSERT INTO CanonicalModelForeignKeys SELECT count(*) FROM pragma_foreign_key_check; DROP TABLE CanonicalModelForeignKeys;");
            migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(FrozenMigrationMetadata.Read("1d67eaa7340220dcb7c698569be5dc995984f5fa4da438fd77d265e7dc2e1e1e"));
            migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
            migrationBuilder.Sql(FrozenMigrationMetadata.Read("aaba7f0481840289b98042a95e13b21e2ceee8808e99282cf8aa89c459008f85"));
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
            migrationBuilder.Sql(FrozenMigrationMetadata.Read("cc09168957bf1fec9c6eea299c783f7f16000a5b53f94a14ce0f4ce0ade532f9"));
            migrationBuilder.Sql(FrozenMigrationMetadata.Read("0a3f6cf71e17ddd0a8e0038fb00092f3cd1362d1cb15297817f4149c8f861d22"));
            migrationBuilder.Sql("CREATE TEMP TABLE CanonicalModelForeignKeys(Rows INTEGER CHECK(Rows=0)); INSERT INTO CanonicalModelForeignKeys SELECT count(*) FROM pragma_foreign_key_check; DROP TABLE CanonicalModelForeignKeys;");
            migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
        }
    }
}
