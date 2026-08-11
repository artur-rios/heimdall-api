"""Unit tests for the pure helpers in migrations.py.

Run from the repository root:  python -m unittest discover -s scripts -p "test_*.py"
"""

import unittest

from migrations import describe_connection, parse_env_file


class ParseEnvFileTests(unittest.TestCase):
    def test_given_quoted_values_when_parsed_then_quotes_are_stripped(self):
        parsed = parse_env_file('HEIMDALL_DATA_DATABASETYPE="PostgreSql"\n')

        self.assertEqual({"HEIMDALL_DATA_DATABASETYPE": "PostgreSql"}, parsed)

    def test_given_a_byte_order_mark_when_parsed_then_the_first_key_is_clean(self):
        parsed = parse_env_file("﻿FIRST=1\n")

        self.assertEqual({"FIRST": "1"}, parsed)

    def test_given_comments_and_blank_lines_when_parsed_then_they_are_skipped(self):
        parsed = parse_env_file("# a comment\n\n  \nKEY=value\n")

        self.assertEqual({"KEY": "value"}, parsed)

    def test_given_a_value_containing_equals_when_parsed_then_the_value_is_intact(self):
        parsed = parse_env_file('CONN="Host=localhost;Database=heimdall"\n')

        self.assertEqual({"CONN": "Host=localhost;Database=heimdall"}, parsed)

    def test_given_a_line_without_a_separator_when_parsed_then_it_is_ignored(self):
        parsed = parse_env_file("NOT_A_PAIR\nKEY=value\n")

        self.assertEqual({"KEY": "value"}, parsed)


class DescribeConnectionTests(unittest.TestCase):
    def test_given_a_password_when_described_then_it_is_masked(self):
        described = describe_connection("Host=localhost;Database=im;Username=app;Password=secret")

        self.assertEqual("Host=localhost;Database=im;Username=app;Password=***", described)

    def test_given_a_password_in_mixed_case_when_described_then_it_is_masked(self):
        described = describe_connection("Host=localhost;PASSWORD=secret")

        self.assertEqual("Host=localhost;PASSWORD=***", described)

    def test_given_a_trailing_separator_when_described_then_no_empty_segment_remains(self):
        described = describe_connection("Host=localhost;")

        self.assertEqual("Host=localhost", described)


if __name__ == "__main__":
    unittest.main()
