﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ErsatzTV.Infrastructure.Migrations
{
    public partial class Reset_AllStatistics20220420 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // reset all local libraries

            migrationBuilder.Sql("UPDATE MediaVersion SET DateUpdated = '0001-01-01 00:00:00' WHERE SongId IS NULL");
            migrationBuilder.Sql("UPDATE LibraryFolder SET Etag = NULL WHERE Id IN (SELECT LF.Id FROM LibraryFolder LF INNER JOIN LibraryPath LP on LP.Id = LF.LibraryPathId INNER JOIN Library L on L.Id = LP.LibraryId WHERE MediaKind != 5)");
            migrationBuilder.Sql("UPDATE LibraryPath SET LastScan = '0001-01-01 00:00:00' WHERE Id IN (SELECT LP.Id FROM LibraryPath LP INNER JOIN Library L on L.Id = LP.LibraryId WHERE MediaKind != 5)");
            migrationBuilder.Sql("UPDATE Library SET LastScan = '0001-01-01 00:00:00' WHERE MediaKind != 5");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}