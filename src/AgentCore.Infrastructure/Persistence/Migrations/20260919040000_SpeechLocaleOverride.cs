using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(AgentCoreDbContext))]
    [Migration("20260919040000_SpeechLocaleOverride")]
    public partial class SpeechLocaleOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SpeechLocaleOverride",
                table: "SessionSnapshots",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpeechLocaleOverride",
                table: "SessionSnapshots");
        }
    }
}
