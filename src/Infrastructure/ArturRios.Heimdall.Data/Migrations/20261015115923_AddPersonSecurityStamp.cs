using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Adds <c>security_stamp</c> to <c>heimdall.person</c>: the value every person token now
    ///     carries, and which a credential change replaces to revoke the tokens issued before it
    ///     (FR-AU-11, Threat Model TH-36).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>gen_random_uuid()</c> is volatile, so PostgreSQL evaluates the default once per
    ///         existing row while it adds the column: every person gets a stamp of their own, and no
    ///         two share one. The default stays for any row inserted without a value; the application
    ///         always supplies one.
    ///     </para>
    ///     <para>
    ///         No token issued before this migration carries a stamp, so each is refused from the
    ///         first request after deployment and every person signs in once more. The <c>person</c>
    ///         table's triggers guard <c>public_id</c> alone (#199), so the column stays writable.
    ///     </para>
    /// </remarks>
    public partial class AddPersonSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "security_stamp",
                schema: "heimdall",
                table: "person",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "security_stamp",
                schema: "heimdall",
                table: "person");
        }
    }
}
