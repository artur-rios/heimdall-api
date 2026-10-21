using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArturRios.Heimdall.Data.Migrations
{
    /// <summary>
    ///     Records the lawful basis on System Admin persons still at Unrecorded, which is where the
    ///     seeder left the master user until it began to record one (NFR-23).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Unlike <c>RecordLegalBasis</c>, which left every earlier identity visibly unrecorded
    ///         because it could not know the basis, this writes one: the seeded master is created by
    ///         this system to run it, so contract performance (the default for an identity outside any
    ///         scope) is what the seeder now records, with the privacy notice version in force.
    ///     </para>
    ///     <para>
    ///         The seeder identifies the master only as "a System Admin that is not deleted", and a
    ///         row does not remember how it was created, so the master cannot be told in SQL from
    ///         another System Admin that predates NFR-23. Every System Admin at
    ///         <c>legal_basis = 0</c> is therefore backfilled. A System Admin created since NFR-23 is
    ///         never at 0, so none that was recorded is overwritten; persons of other roles and
    ///         Google Users are left alone. <c>Down</c> does not undo the data change: the rows
    ///         cannot be told apart afterwards from ones recorded by the seeder.
    ///     </para>
    /// </remarks>
    public partial class BackfillSeededMasterLegalBasis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1 = Roles.SystemAdmin, 1 = LegalBases.ContractPerformance; "1.2" is the privacy notice
            // version in force (LegalBasisRecorder.CurrentPrivacyNoticeVersion). Literals, because a
            // migration must keep meaning what it meant when it was written.
            migrationBuilder.Sql("""
                UPDATE heimdall.person
                SET legal_basis = 1,
                    privacy_notice_version = '1.2',
                    basis_recorded_at = now()
                WHERE role_id = 1
                  AND legal_basis = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: see the remarks.
        }
    }
}
