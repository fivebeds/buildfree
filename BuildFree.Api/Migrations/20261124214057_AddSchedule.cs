using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildFree.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WorkDays",
                table: "Accounts",
                type: "integer",
                nullable: false,
                defaultValue: 62);

            migrationBuilder.CreateTable(
                name: "NonWorkdays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NonWorkdays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AssigneeIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'"),
                    Done = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleItems_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NonWorkdays_AccountId_Date",
                table: "NonWorkdays",
                columns: new[] { "AccountId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleItems_AccountId_ProjectId",
                table: "ScheduleItems",
                columns: new[] { "AccountId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleItems_AccountId_StartDate",
                table: "ScheduleItems",
                columns: new[] { "AccountId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleItems_ProjectId",
                table: "ScheduleItems",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NonWorkdays");

            migrationBuilder.DropTable(
                name: "ScheduleItems");

            migrationBuilder.DropColumn(
                name: "WorkDays",
                table: "Accounts");
        }
    }
}
