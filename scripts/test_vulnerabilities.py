"""Unit tests for the parser in vulnerabilities.py.

Run from the repository root:  python -m unittest discover -s scripts -p "test_*.py"

The parser is the whole check. `dotnet list package --vulnerable` exits 0 whether it found
anything or not, so nothing but this parsing stands between a published CVE and a green build --
which makes a parser that silently returns nothing the exact shape of the failure it exists to
prevent. The clean-tree case is tested as carefully as the vulnerable one for that reason.
"""

import unittest

from vulnerabilities import describe, parse

CLEAN = """\
  Determining projects to restore...
  All projects are up-to-date for restore.

The following sources were used:
   https://api.nuget.org/v3/index.json

The given project `ArturRios.Heimdall.WebApi` has no vulnerable packages given the current sources.
The given project `ArturRios.Heimdall.Domain` has no vulnerable packages given the current sources.
"""

VULNERABLE = """\
The following sources were used:
   https://api.nuget.org/v3/index.json

The given project `ArturRios.Heimdall.WebApi` has the following vulnerable packages
   [net10.0]:
   Top-level Package      Requested   Resolved   Severity   Advisory URL
   > SSH.NET              2024.1.0    2024.1.0   High       https://github.com/advisories/GHSA-1

   Transitive Package               Resolved   Severity   Advisory URL
   > System.Text.Json               8.0.0      Critical   https://github.com/advisories/GHSA-2
"""


# Captured verbatim from `dotnet list <project> package --vulnerable --include-transitive` against a
# throwaway project referencing the SSH.NET version this repository actually shipped with, trailing
# whitespace and all. A hand-written fixture agreeing with a hand-written parser proves only that one
# person guessed consistently twice.
REAL_SINGLE_PROJECT = """\
The following sources were used:
   https://api.nuget.org/v3/index.json

Project `vulncheck` has the following vulnerable packages
   [net10.0]:
   Top-level Package      Requested   Resolved   Severity   Advisory URL
   > SSH.NET              2024.1.0    2024.1.0   High       https://github.com/advisories/GHSA-q939-rpr3-3284
"""


class ParseTests(unittest.TestCase):
    def test_given_the_single_project_wording_when_parsed_then_the_project_is_attributed(self):
        # `dotnet list` says "The given project `x`" for a solution and "Project `x`" for a single
        # project. A pattern that matches only the first still reports the finding -- the rows are
        # read independently -- but blames it on nothing, which is how this was caught.
        findings = parse(REAL_SINGLE_PROJECT)

        self.assertEqual(1, len(findings))
        self.assertEqual("vulncheck", findings[0].project)
        self.assertEqual("SSH.NET", findings[0].package)
        self.assertEqual("High", findings[0].severity)

    def test_given_a_clean_tree_when_parsed_then_there_are_no_findings(self):
        self.assertEqual([], parse(CLEAN))

    def test_given_no_output_at_all_when_parsed_then_there_are_no_findings(self):
        self.assertEqual([], parse(""))

    def test_given_a_top_level_vulnerability_when_parsed_then_its_columns_are_read(self):
        finding = next(f for f in parse(VULNERABLE) if f.package == "SSH.NET")

        self.assertEqual("ArturRios.Heimdall.WebApi", finding.project)
        self.assertEqual("2024.1.0", finding.resolved)
        self.assertEqual("High", finding.severity)
        self.assertEqual("https://github.com/advisories/GHSA-1", finding.advisory)

    def test_given_a_transitive_vulnerability_when_parsed_then_the_missing_column_does_not_shift_it(self):
        # A transitive row has no Requested column. Reading from the left would report the resolved
        # version as the severity and quietly mislabel every transitive finding -- the kind that
        # this project actually shipped.
        finding = next(f for f in parse(VULNERABLE) if f.package == "System.Text.Json")

        self.assertEqual("8.0.0", finding.resolved)
        self.assertEqual("Critical", finding.severity)
        self.assertEqual("https://github.com/advisories/GHSA-2", finding.advisory)

    def test_given_both_shapes_when_parsed_then_every_row_is_found(self):
        self.assertEqual(2, len(parse(VULNERABLE)))

    def test_given_a_header_row_when_parsed_then_it_is_not_a_finding(self):
        # The column headers sit directly above the rows and have a similar width; only the `>`
        # marker separates them.
        self.assertEqual([], parse("   Top-level Package      Requested   Resolved   Severity   Advisory URL\n"))


class DescribeTests(unittest.TestCase):
    def test_given_mixed_severities_when_described_then_the_worst_is_first(self):
        described = describe(parse(VULNERABLE))

        self.assertIn("System.Text.Json", described[0])
        self.assertIn("SSH.NET", described[1])

    def test_given_an_unknown_severity_when_described_then_it_sorts_last_rather_than_failing(self):
        unknown = parse(VULNERABLE.replace("High", "Whatever"))

        self.assertIn("System.Text.Json", describe(unknown)[0])
        self.assertEqual(2, len(describe(unknown)))


if __name__ == "__main__":
    unittest.main()
