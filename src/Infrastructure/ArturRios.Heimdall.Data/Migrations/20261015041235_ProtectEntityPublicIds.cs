using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Guards <c>public_id</c> on every other table the identity check trusts: <c>scope</c>,
    ///     <c>application</c>, <c>application_credential</c>, <c>scope_permission</c>, <c>role</c> and
    ///     <c>pending_registration</c> become immutable, and refuse a new row whose id another guarded
    ///     table holds (#199, R2 and R3; Threat Model TH-18 and §6.1).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>ProtectIdentityPublicIds</c> refuses a new person or Google User whose
    ///         <c>public_id</c> another entity holds, but it can only see who holds an id at that
    ///         moment, and only when the identity comes second. Two DML paths were left. While these
    ///         tables' ids could be rewritten, a scope could be moved off its id, a person inserted with
    ///         the freed id and anonymised, every audit target naming the scope cleared, and the scope
    ///         moved back. And while only the identity tables were checked, a person could be inserted
    ///         with a fresh id first and a scope with the same id after it, so that every entry later
    ///         naming the scope was clearable through the person.
    ///     </para>
    ///     <para>
    ///         Each table therefore gets the same two row-level triggers as the identity tables:
    ///         <c>*_public_id_is_immutable</c> (<c>BEFORE UPDATE</c>, <c>public_id_is_immutable()</c>)
    ///         and <c>*_public_id_is_unused</c> (<c>BEFORE INSERT</c>, <c>public_id_is_unused()</c>).
    ///         The functions name the table through <c>TG_TABLE_NAME</c> and leave its own table to its
    ///         unique index, so they need no change. Their audit-trail check applies to the identity
    ///         tables only, as that function's comment explains. Every other column stays writable.
    ///         The application never changes these ids and always creates them fresh with
    ///         <c>Guid.NewGuid()</c>, the seeder's roles included, and its role realignment writes only
    ///         the name and description. Down drops the twelve triggers; the functions stay with the
    ///         migration that created them.
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
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_immutable();

                CREATE TRIGGER scope_public_id_is_unused
                BEFORE INSERT ON heimdall.scope
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_unused();

                CREATE TRIGGER application_public_id_is_immutable
                BEFORE UPDATE ON heimdall.application
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_immutable();

                CREATE TRIGGER application_public_id_is_unused
                BEFORE INSERT ON heimdall.application
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_unused();

                CREATE TRIGGER application_credential_public_id_is_immutable
                BEFORE UPDATE ON heimdall.application_credential
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_immutable();

                CREATE TRIGGER application_credential_public_id_is_unused
                BEFORE INSERT ON heimdall.application_credential
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_unused();

                CREATE TRIGGER scope_permission_public_id_is_immutable
                BEFORE UPDATE ON heimdall.scope_permission
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_immutable();

                CREATE TRIGGER scope_permission_public_id_is_unused
                BEFORE INSERT ON heimdall.scope_permission
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_unused();

                CREATE TRIGGER role_public_id_is_immutable
                BEFORE UPDATE ON heimdall.role
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_immutable();

                CREATE TRIGGER role_public_id_is_unused
                BEFORE INSERT ON heimdall.role
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_unused();

                CREATE TRIGGER pending_registration_public_id_is_immutable
                BEFORE UPDATE ON heimdall.pending_registration
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_immutable();

                CREATE TRIGGER pending_registration_public_id_is_unused
                BEFORE INSERT ON heimdall.pending_registration
                FOR EACH ROW EXECUTE FUNCTION heimdall.public_id_is_unused();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS pending_registration_public_id_is_unused ON heimdall.pending_registration;
                DROP TRIGGER IF EXISTS pending_registration_public_id_is_immutable ON heimdall.pending_registration;
                DROP TRIGGER IF EXISTS role_public_id_is_unused ON heimdall.role;
                DROP TRIGGER IF EXISTS role_public_id_is_immutable ON heimdall.role;
                DROP TRIGGER IF EXISTS scope_permission_public_id_is_unused ON heimdall.scope_permission;
                DROP TRIGGER IF EXISTS scope_permission_public_id_is_immutable ON heimdall.scope_permission;
                DROP TRIGGER IF EXISTS application_credential_public_id_is_unused ON heimdall.application_credential;
                DROP TRIGGER IF EXISTS application_credential_public_id_is_immutable ON heimdall.application_credential;
                DROP TRIGGER IF EXISTS application_public_id_is_unused ON heimdall.application;
                DROP TRIGGER IF EXISTS application_public_id_is_immutable ON heimdall.application;
                DROP TRIGGER IF EXISTS scope_public_id_is_unused ON heimdall.scope;
                DROP TRIGGER IF EXISTS scope_public_id_is_immutable ON heimdall.scope;
                """);
        }
    }
}
