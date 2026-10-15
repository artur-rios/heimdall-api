using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Makes <c>public_id</c> immutable on every table the identity insert check consults:
    ///     <c>scope</c>, <c>application</c>, <c>application_credential</c>, <c>scope_permission</c>,
    ///     <c>role</c> and <c>pending_registration</c> (#199, R2; Threat Model TH-18 and §6.1).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>ProtectIdentityPublicIds</c> refuses a new person or Google User whose
    ///         <c>public_id</c> another entity holds, but it can only see who holds an id at that
    ///         moment. While these tables' ids could be rewritten, the check could be walked round:
    ///         move a scope off its id, insert a person with the freed id, anonymise or tombstone them,
    ///         clear every audit target naming the scope, and move the scope back. Fixing the id on
    ///         every table the check trusts refuses the first step, so an audit target can only be
    ///         cleared for an id that has always been a person's or a Google User's.
    ///     </para>
    ///     <para>
    ///         Each table gets a row-level <c>BEFORE UPDATE</c> trigger, <c>*_public_id_is_immutable</c>,
    ///         bound to the same <c>heimdall.identity_public_id_is_immutable()</c> the identity tables
    ///         use; its message names the table through <c>TG_TABLE_NAME</c>, so it needs no change.
    ///         Every other column stays writable. The application never changes these ids: each is a
    ///         fresh <c>Guid.NewGuid()</c> at creation, and the seeder's role realignment writes only
    ///         the name and description. Down drops the six triggers; the function stays with the
    ///         migration that created it.
    ///     </para>
    /// </remarks>
    public partial class ProtectEntityPublicIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TRIGGER scope_public_id_is_immutable
                BEFORE UPDATE ON heimdall.scope
                FOR EACH ROW EXECUTE FUNCTION heimdall.identity_public_id_is_immutable();

                CREATE TRIGGER application_public_id_is_immutable
                BEFORE UPDATE ON heimdall.application
                FOR EACH ROW EXECUTE FUNCTION heimdall.identity_public_id_is_immutable();

                CREATE TRIGGER application_credential_public_id_is_immutable
                BEFORE UPDATE ON heimdall.application_credential
                FOR EACH ROW EXECUTE FUNCTION heimdall.identity_public_id_is_immutable();

                CREATE TRIGGER scope_permission_public_id_is_immutable
                BEFORE UPDATE ON heimdall.scope_permission
                FOR EACH ROW EXECUTE FUNCTION heimdall.identity_public_id_is_immutable();

                CREATE TRIGGER role_public_id_is_immutable
                BEFORE UPDATE ON heimdall.role
                FOR EACH ROW EXECUTE FUNCTION heimdall.identity_public_id_is_immutable();

                CREATE TRIGGER pending_registration_public_id_is_immutable
                BEFORE UPDATE ON heimdall.pending_registration
                FOR EACH ROW EXECUTE FUNCTION heimdall.identity_public_id_is_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS pending_registration_public_id_is_immutable ON heimdall.pending_registration;
                DROP TRIGGER IF EXISTS role_public_id_is_immutable ON heimdall.role;
                DROP TRIGGER IF EXISTS scope_permission_public_id_is_immutable ON heimdall.scope_permission;
                DROP TRIGGER IF EXISTS application_credential_public_id_is_immutable ON heimdall.application_credential;
                DROP TRIGGER IF EXISTS application_public_id_is_immutable ON heimdall.application;
                DROP TRIGGER IF EXISTS scope_public_id_is_immutable ON heimdall.scope;
                """);
        }
    }
}
