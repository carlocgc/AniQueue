using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AniQueue.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Franchises",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ManualSortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Franchises", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Profiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Anime",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AlternativeTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeCount = table.Column<int>(type: "INTEGER", nullable: true),
                    EpisodeDurationMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    ReleaseYear = table.Column<int>(type: "INTEGER", nullable: true),
                    CoverImageUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceAnimeId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FranchiseId = table.Column<int>(type: "INTEGER", nullable: true),
                    FranchiseOrder = table.Column<int>(type: "INTEGER", nullable: true),
                    OptionalWithinFranchise = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Anime", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Anime_Franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "Franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ProfileSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DefaultQueueSize = table.Column<int>(type: "INTEGER", nullable: false),
                    DateFormat = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Theme = table.Column<int>(type: "INTEGER", nullable: false),
                    ShowOptionalFranchiseEntries = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultRecommendationMode = table.Column<int>(type: "INTEGER", nullable: false),
                    IncludePersonalNotesInAiExport = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileSettings_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecommendationRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ProviderName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModelIdentifier = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CompletedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CandidateCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WasApplied = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecommendationRuns_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LibraryEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    AnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    UserScore = table.Column<int>(type: "INTEGER", nullable: true),
                    EpisodesWatched = table.Column<int>(type: "INTEGER", nullable: false),
                    DateStarted = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    DateCompleted = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    DateAdded = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUpdated = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PersonalNotes = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    ManualPriority = table.Column<int>(type: "INTEGER", nullable: false),
                    IsHidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecommendationScore = table.Column<double>(type: "REAL", nullable: true),
                    RecommendationConfidence = table.Column<double>(type: "REAL", nullable: true),
                    RecommendationReason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RecommendationUpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryEntries", x => x.Id);
                    table.CheckConstraint("CK_LibraryEntries_UserScoreRange", "\"UserScore\" IS NULL OR (\"UserScore\" >= 1 AND \"UserScore\" <= 10)");
                    table.ForeignKey(
                        name: "FK_LibraryEntries_Anime_AnimeId",
                        column: x => x.AnimeId,
                        principalTable: "Anime",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LibraryEntries_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QueueItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    AnimeId = table.Column<int>(type: "INTEGER", nullable: true),
                    FranchiseId = table.Column<int>(type: "INTEGER", nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueItems", x => x.Id);
                    table.CheckConstraint("CK_QueueItems_AnimeXorFranchise", "(\"AnimeId\" IS NULL) <> (\"FranchiseId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_QueueItems_Anime_AnimeId",
                        column: x => x.AnimeId,
                        principalTable: "Anime",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QueueItems_Franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "Franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QueueItems_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecommendationRunItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    AnimeId = table.Column<int>(type: "INTEGER", nullable: true),
                    FranchiseId = table.Column<int>(type: "INTEGER", nullable: true),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    PredictedScore = table.Column<double>(type: "REAL", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationRunItems", x => x.Id);
                    table.CheckConstraint("CK_RecommendationRunItems_AnimeXorFranchise", "(\"AnimeId\" IS NULL) <> (\"FranchiseId\" IS NULL)");
                    table.CheckConstraint("CK_RecommendationRunItems_ConfidenceRange", "\"Confidence\" >= 0.0 AND \"Confidence\" <= 1.0");
                    table.CheckConstraint("CK_RecommendationRunItems_RankPositive", "\"Rank\" >= 1");
                    table.ForeignKey(
                        name: "FK_RecommendationRunItems_Anime_AnimeId",
                        column: x => x.AnimeId,
                        principalTable: "Anime",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecommendationRunItems_Franchises_FranchiseId",
                        column: x => x.FranchiseId,
                        principalTable: "Franchises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecommendationRunItems_RecommendationRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "RecommendationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Anime_FranchiseId",
                table: "Anime",
                column: "FranchiseId");

            migrationBuilder.CreateIndex(
                name: "IX_Anime_Source_SourceAnimeId",
                table: "Anime",
                columns: new[] { "Source", "SourceAnimeId" },
                unique: true,
                filter: "\"SourceAnimeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Anime_Title",
                table: "Anime",
                column: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_Franchises_ManualSortOrder",
                table: "Franchises",
                column: "ManualSortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_Franchises_Name",
                table: "Franchises",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_AnimeId",
                table: "LibraryEntries",
                column: "AnimeId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_ProfileId_AnimeId",
                table: "LibraryEntries",
                columns: new[] { "ProfileId", "AnimeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_ProfileId_IsHidden",
                table: "LibraryEntries",
                columns: new[] { "ProfileId", "IsHidden" });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_ProfileId_RecommendationScore",
                table: "LibraryEntries",
                columns: new[] { "ProfileId", "RecommendationScore" });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_ProfileId_Status",
                table: "LibraryEntries",
                columns: new[] { "ProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_Name",
                table: "Profiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSettings_ProfileId",
                table: "ProfileSettings",
                column: "ProfileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_AnimeId",
                table: "QueueItems",
                column: "AnimeId");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_FranchiseId",
                table: "QueueItems",
                column: "FranchiseId");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_ProfileId_AnimeId",
                table: "QueueItems",
                columns: new[] { "ProfileId", "AnimeId" },
                unique: true,
                filter: "\"AnimeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_ProfileId_FranchiseId",
                table: "QueueItems",
                columns: new[] { "ProfileId", "FranchiseId" },
                unique: true,
                filter: "\"FranchiseId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_ProfileId_Position",
                table: "QueueItems",
                columns: new[] { "ProfileId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationRunItems_AnimeId",
                table: "RecommendationRunItems",
                column: "AnimeId");

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationRunItems_FranchiseId",
                table: "RecommendationRunItems",
                column: "FranchiseId");

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationRunItems_RunId_Rank",
                table: "RecommendationRunItems",
                columns: new[] { "RunId", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationRuns_ProfileId_CreatedAt",
                table: "RecommendationRuns",
                columns: new[] { "ProfileId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryEntries");

            migrationBuilder.DropTable(
                name: "ProfileSettings");

            migrationBuilder.DropTable(
                name: "QueueItems");

            migrationBuilder.DropTable(
                name: "RecommendationRunItems");

            migrationBuilder.DropTable(
                name: "Anime");

            migrationBuilder.DropTable(
                name: "RecommendationRuns");

            migrationBuilder.DropTable(
                name: "Franchises");

            migrationBuilder.DropTable(
                name: "Profiles");
        }
    }
}
