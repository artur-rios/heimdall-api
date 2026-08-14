using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Replaces the plaintext password-reset and email-verification tokens with their SHA-256
    ///     digests (Threat Model TH-14).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Hand-written rather than left as scaffolded. The generated version dropped the token
    ///         column and added <c>token_hash</c> with an empty-array default, which would have given
    ///         every existing row the same value and failed the unique index the moment a table held
    ///         more than one row — and silently invalidated every live token on a table that held
    ///         exactly one.
    ///     </para>
    ///     <para>
    ///         Instead the digest is computed from the column being replaced, in place, so links
    ///         already sitting in people's inboxes keep working. A verification token lives for a day
    ///         by default, so dropping them would have meant a day of failed verifications for
    ///         everybody mid-flow.
    ///     </para>
    ///     <para>
    ///         <c>sha256(bytea)</c> is core PostgreSQL from version 11 and needs no extension. It must
    ///         agree exactly with <c>SingleUseTokenHash.Of</c>, which is SHA-256 over the token's
    ///         UTF-8 bytes rendered as lowercase hex — hence <c>convert_to(token, 'UTF8')</c> rather
    ///         than a cast, and <c>encode(..., 'hex')</c>, which PostgreSQL emits in lower case.
    ///     </para>
    /// </remarks>
    public partial class HashSingleUseTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable first, so existing rows can be filled before the constraint applies.
            migrationBuilder.AddColumn<string>(
                name: "token_hash",
                schema: "heimdall",
                table: "password_reset_token",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "token_hash",
                schema: "heimdall",
                table: "email_verification_token",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE heimdall.password_reset_token
                SET token_hash = encode(sha256(convert_to(token, 'UTF8')), 'hex');
                """);

            migrationBuilder.Sql(
                """
                UPDATE heimdall.email_verification_token
                SET token_hash = encode(sha256(convert_to(token, 'UTF8')), 'hex');
                """);

            migrationBuilder.DropIndex(
                name: "ix_password_reset_token_token",
                schema: "heimdall",
                table: "password_reset_token");

            migrationBuilder.DropIndex(
                name: "ix_email_verification_token_token",
                schema: "heimdall",
                table: "email_verification_token");

            // The plaintext leaves the database here, which is the whole point of the migration.
            migrationBuilder.DropColumn(
                name: "token",
                schema: "heimdall",
                table: "password_reset_token");

            migrationBuilder.DropColumn(
                name: "token",
                schema: "heimdall",
                table: "email_verification_token");

            // Raw SQL rather than AlterColumn, and not by preference. AlterColumn compares against
            // the model the migration started from, where token_hash did not exist at all, so it
            // emitted nothing whatsoever — no error, just a column left nullable while the entity
            // and the snapshot both declared it required. Verified by reading the generated script
            // and the resulting table; the scaffolded call looked correct and did nothing.
            migrationBuilder.Sql(
                "ALTER TABLE heimdall.password_reset_token ALTER COLUMN token_hash SET NOT NULL;");

            migrationBuilder.Sql(
                "ALTER TABLE heimdall.email_verification_token ALTER COLUMN token_hash SET NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_token_token_hash",
                schema: "heimdall",
                table: "password_reset_token",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_verification_token_token_hash",
                schema: "heimdall",
                table: "email_verification_token",
                column: "token_hash",
                unique: true);
        }

        /// <summary>
        ///     Restores the plaintext columns, and discards every token row to do it.
        /// </summary>
        /// <remarks>
        ///     A hash cannot be inverted, so there is no honest way to put back what Up removed. The
        ///     alternatives were to leave the rows with an empty token — which the unique index would
        ///     reject for the second row, and which would silently accept an empty string as a valid
        ///     token for the first — or to delete them. Deleting is the one that fails safe: a person
        ///     mid-reset requests another email, and the rows were expiring within the day anyway.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM heimdall.password_reset_token;");
            migrationBuilder.Sql("DELETE FROM heimdall.email_verification_token;");

            migrationBuilder.DropIndex(
                name: "ix_password_reset_token_token_hash",
                schema: "heimdall",
                table: "password_reset_token");

            migrationBuilder.DropIndex(
                name: "ix_email_verification_token_token_hash",
                schema: "heimdall",
                table: "email_verification_token");

            migrationBuilder.DropColumn(
                name: "token_hash",
                schema: "heimdall",
                table: "password_reset_token");

            migrationBuilder.DropColumn(
                name: "token_hash",
                schema: "heimdall",
                table: "email_verification_token");

            migrationBuilder.AddColumn<string>(
                name: "token",
                schema: "heimdall",
                table: "password_reset_token",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "token",
                schema: "heimdall",
                table: "email_verification_token",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_token_token",
                schema: "heimdall",
                table: "password_reset_token",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_verification_token_token",
                schema: "heimdall",
                table: "email_verification_token",
                column: "token",
                unique: true);
        }
    }
}
