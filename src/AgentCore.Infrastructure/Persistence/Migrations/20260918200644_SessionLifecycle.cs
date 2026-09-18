using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentCompletion",
                table: "SessionSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeadlineAtUtc",
                table: "SessionSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LifecycleChangedAtUtc",
                table: "SessionSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LifecycleReason",
                table: "SessionSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LifecycleSource",
                table: "SessionSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LifecycleStatus",
                table: "SessionSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PurposeDescription",
                table: "SessionSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PurposeKind",
                table: "SessionSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PurposeMetadataJson",
                table: "SessionSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UserCancellationAllowed",
                table: "SessionSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UserCompletionAllowed",
                table: "SessionSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE SessionSnapshots
                SET LifecycleStatus = CASE (
                        SELECT Status FROM Sessions WHERE Sessions.SessionId = SessionSnapshots.SessionId
                    )
                    WHEN 'Paused' THEN 'Paused'
                    WHEN 'Ended' THEN 'Ended'
                    ELSE 'Active'
                END
                WHERE LifecycleStatus IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentCompletion",
                table: "SessionSnapshots");

            migrationBuilder.DropColumn(
                name: "DeadlineAtUtc",
                table: "SessionSnapshots");

            migrationBuilder.DropColumn(
                name: "LifecycleChangedAtUtc",
                table: "SessionSnapshots");

            migrationBuilder.DropColumn(
                name: "LifecycleReason",
                table: "SessionSnapshots");

            migrationBuilder.DropColumn(
                name: "LifecycleSource",
                table: "SessionSnapshots");

            migrationBuilder.DropColumn(
                name: "LifecycleStatus",
                table: "SessionSnapshots");

            migrationBuilder.DropColumn(
                name: "PurposeDescription",
                table: "SessionSnapshots");

            migrationBuilder.DropColumn(
                name: "PurposeKind",
                table: "SessionSnapshots");

            migrationBuilder.DropColumn(
                name: "PurposeMetadataJson",
                table: "SessionSnapshots");

            migrationBuilder.DropColumn(
                name: "UserCancellationAllowed",
                table: "SessionSnapshots");

            migrationBuilder.DropColumn(
                name: "UserCompletionAllowed",
                table: "SessionSnapshots");
        }
    }
}
