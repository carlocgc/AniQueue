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
                name: "Anime",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    TitleRomaji = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TitleEnglish = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TitleNative = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeCount = table.Column<int>(type: "INTEGER", nullable: true),
                    EpisodeDurationMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    ReleaseYear = table.Column<int>(type: "INTEGER", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CoverImageColor = table.Column<string>(type: "TEXT", maxLength: 7, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Anime", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnimeRelations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RelationType = table.Column<int>(type: "INTEGER", nullable: false),
                    RelatedExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimeRelations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Genres",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Genres", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UnitKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Trigger = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsProcessed = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsChanged = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Profiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LibraryKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Studios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsAnimationStudio = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Studios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnimeExternalIds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MissingFromSourceAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RelationsFetchedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimeExternalIds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnimeExternalIds_Anime_AnimeId",
                        column: x => x.AnimeId,
                        principalTable: "Anime",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnimeImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Rendition = table.Column<int>(type: "INTEGER", nullable: false),
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

            migrationBuilder.CreateTable(
                name: "AnimeGenres",
                columns: table => new
                {
                    AnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                    GenreId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimeGenres", x => new { x.AnimeId, x.GenreId });
                    table.ForeignKey(
                        name: "FK_AnimeGenres_Anime_AnimeId",
                        column: x => x.AnimeId,
                        principalTable: "Anime",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AnimeGenres_Genres_GenreId",
                        column: x => x.GenreId,
                        principalTable: "Genres",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                    LastWrittenBySource = table.Column<int>(type: "INTEGER", nullable: true),
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
                    PreferredTitleLanguage = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultRecommendationMode = table.Column<int>(type: "INTEGER", nullable: false)
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
                name: "QueueItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    AnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueueItems_Anime_AnimeId",
                        column: x => x.AnimeId,
                        principalTable: "Anime",
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
                    WasApplied = table.Column<bool>(type: "INTEGER", nullable: false),
                    DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: true)
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
                name: "SyncRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    Created = table.Column<int>(type: "INTEGER", nullable: false),
                    Updated = table.Column<int>(type: "INTEGER", nullable: false),
                    Skipped = table.Column<int>(type: "INTEGER", nullable: false),
                    ConflictsHeld = table.Column<int>(type: "INTEGER", nullable: false),
                    SlotsReleased = table.Column<int>(type: "INTEGER", nullable: false),
                    ChangesHeld = table.Column<int>(type: "INTEGER", nullable: false),
                    AbsentFlagged = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuns_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnimeStudios",
                columns: table => new
                {
                    AnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                    StudioId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsMain = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimeStudios", x => new { x.AnimeId, x.StudioId });
                    table.ForeignKey(
                        name: "FK_AnimeStudios_Anime_AnimeId",
                        column: x => x.AnimeId,
                        principalTable: "Anime",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AnimeStudios_Studios_StudioId",
                        column: x => x.StudioId,
                        principalTable: "Studios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecommendationRunItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    AnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                    PredictedScore = table.Column<double>(type: "REAL", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationRunItems", x => x.Id);
                    table.CheckConstraint("CK_RecommendationRunItems_ConfidenceRange", "\"Confidence\" >= 0.0 AND \"Confidence\" <= 1.0");
                    table.ForeignKey(
                        name: "FK_RecommendationRunItems_Anime_AnimeId",
                        column: x => x.AnimeId,
                        principalTable: "Anime",
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
                name: "IX_Anime_Title",
                table: "Anime",
                column: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_AnimeExternalIds_AnimeId_Source",
                table: "AnimeExternalIds",
                columns: new[] { "AnimeId", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnimeExternalIds_Source_ExternalId",
                table: "AnimeExternalIds",
                columns: new[] { "Source", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnimeGenres_GenreId",
                table: "AnimeGenres",
                column: "GenreId");

            migrationBuilder.CreateIndex(
                name: "IX_AnimeImages_AnimeId_Kind_Source_Rendition",
                table: "AnimeImages",
                columns: new[] { "AnimeId", "Kind", "Source", "Rendition" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnimeRelations_Source_ExternalId_RelationType_RelatedExternalId",
                table: "AnimeRelations",
                columns: new[] { "Source", "ExternalId", "RelationType", "RelatedExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnimeRelations_Source_RelatedExternalId",
                table: "AnimeRelations",
                columns: new[] { "Source", "RelatedExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_AnimeStudios_StudioId",
                table: "AnimeStudios",
                column: "StudioId");

            migrationBuilder.CreateIndex(
                name: "IX_Genres_Name",
                table: "Genres",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_TaskKey_UnitKey_Id",
                table: "JobRuns",
                columns: new[] { "TaskKey", "UnitKey", "Id" });

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
                name: "IX_QueueItems_ProfileId_AnimeId",
                table: "QueueItems",
                columns: new[] { "ProfileId", "AnimeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_ProfileId_Position",
                table: "QueueItems",
                columns: new[] { "ProfileId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationRunItems_AnimeId",
                table: "RecommendationRunItems",
                column: "AnimeId");

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationRunItems_RunId",
                table: "RecommendationRunItems",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationRuns_ProfileId_CreatedAt",
                table: "RecommendationRuns",
                columns: new[] { "ProfileId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Studios_Name",
                table: "Studios",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_ProfileId_Source",
                table: "SyncRuns",
                columns: new[] { "ProfileId", "Source" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnimeExternalIds");

            migrationBuilder.DropTable(
                name: "AnimeGenres");

            migrationBuilder.DropTable(
                name: "AnimeImages");

            migrationBuilder.DropTable(
                name: "AnimeRelations");

            migrationBuilder.DropTable(
                name: "AnimeStudios");

            migrationBuilder.DropTable(
                name: "JobRuns");

            migrationBuilder.DropTable(
                name: "LibraryEntries");

            migrationBuilder.DropTable(
                name: "ProfileSettings");

            migrationBuilder.DropTable(
                name: "QueueItems");

            migrationBuilder.DropTable(
                name: "RecommendationRunItems");

            migrationBuilder.DropTable(
                name: "SyncRuns");

            migrationBuilder.DropTable(
                name: "Genres");

            migrationBuilder.DropTable(
                name: "Studios");

            migrationBuilder.DropTable(
                name: "Anime");

            migrationBuilder.DropTable(
                name: "RecommendationRuns");

            migrationBuilder.DropTable(
                name: "Profiles");
        }
    }
}
