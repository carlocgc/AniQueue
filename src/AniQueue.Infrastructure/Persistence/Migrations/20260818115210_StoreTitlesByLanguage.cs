using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoreTitlesByLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AlternativeTitle is dropped rather than renamed, which is what the
            // scaffolder proposed. It held whichever variant happened to differ from
            // the displayed one — English for some rows, native for others, with
            // nothing recording which. Renaming it to TitleRomaji would assert those
            // values are romaji, which is precisely the guess these columns exist to
            // stop (D22).
            //
            // Nothing is lost that can be recovered anyway: Title is untouched, so
            // every library reads exactly as it did, and the next sync fills the
            // variants in properly. Until then a title has one name, which is the
            // same position a MyAnimeList-only library is in permanently.
            migrationBuilder.DropColumn(
                name: "AlternativeTitle",
                table: "Anime");

            migrationBuilder.AddColumn<string>(
                name: "TitleRomaji",
                table: "Anime",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TitleEnglish",
                table: "Anime",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TitleNative",
                table: "Anime",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "TitleRomaji", table: "Anime");
            migrationBuilder.DropColumn(name: "TitleEnglish", table: "Anime");
            migrationBuilder.DropColumn(name: "TitleNative", table: "Anime");

            migrationBuilder.AddColumn<string>(
                name: "AlternativeTitle",
                table: "Anime",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }
    }
}
