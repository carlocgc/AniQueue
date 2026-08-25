using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Carries out D47: a title's pictures become rows, and the column that held one
    /// of them goes.
    /// </summary>
    /// <remarks>
    /// <b>The old URLs are deliberately not carried across.</b> Copying them into the
    /// new table would look like the careful thing to do and would import the exact
    /// bug this change exists to fix: every one of them names AniList's
    /// <c>extraLarge</c> size, which is 83 KB of a picture rendered in a column forty
    /// pixels wide. Rewriting the paths on the way over was the alternative and was
    /// declined — it is string surgery on a third party's URL scheme, which breaks
    /// silently and library-wide the day they change it.
    ///
    /// So a library that has already synced shows colour blocks until its next sync,
    /// which inserts a row per title at the right size. That is a few minutes of
    /// degradation, once, against a wrong address stored permanently — and it is only
    /// possible because inserting a missing row is not the same operation as
    /// overwriting a scalar, so it happens without any of the field-preservation
    /// rules D18 and D21 depend on having to make an exception.
    ///
    /// <c>Down</c> restores the column empty. There is nothing to put back in it: the
    /// values it held were dropped on the way out, on purpose.
    /// </remarks>
    public partial class ArtworkGetsItsOwnTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverImageUrl",
                table: "Anime");

            migrationBuilder.CreateTable(
                name: "AnimeImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    RemoteUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FetchedUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    FileExtension = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    ByteCount = table.Column<long>(type: "INTEGER", nullable: true),
                    FetchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FailedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FailureIsPermanent = table.Column<bool>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimeImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnimeImages_Anime_AnimeId",
                        column: x => x.AnimeId,
                        principalTable: "Anime",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnimeImages_AnimeId_Kind_Source",
                table: "AnimeImages",
                columns: new[] { "AnimeId", "Kind", "Source" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnimeImages");

            migrationBuilder.AddColumn<string>(
                name: "CoverImageUrl",
                table: "Anime",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);
        }
    }
}
