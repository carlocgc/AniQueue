using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConfigurableScoringRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Null means every waiting title, which is what a request carried before
            // it could be capped. An existing profile therefore keeps its behaviour by
            // having nothing written to it at all.
            migrationBuilder.AddColumn<int>(
                name: "RecommendationCandidateLimit",
                table: "ProfileSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecommendationHistorySize",
                table: "ProfileSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Backfilled, and this statement is why the file is hand-edited. The
            // property's default is 200, but a CLR default only reaches rows this
            // build constructs — every profile that already exists would have been
            // left at the zero above, which is not an absence but a real setting
            // meaning "send no history at all". Upgrading would silently have made
            // every future ranking general rather than personal, with nothing on
            // screen saying why.
            //
            // A statement rather than a column default, so the schema carries no
            // constraint the model does not know about, and so that zero stays
            // reachable for someone who genuinely wants it.
            migrationBuilder.Sql(
                "UPDATE ProfileSettings SET RecommendationHistorySize = 200 WHERE RecommendationHistorySize = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecommendationCandidateLimit",
                table: "ProfileSettings");

            migrationBuilder.DropColumn(
                name: "RecommendationHistorySize",
                table: "ProfileSettings");
        }
    }
}
