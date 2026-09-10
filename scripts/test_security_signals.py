"""Unit tests for the collector in security_signals.py.

Run from the repository root:  python -m unittest discover -s scripts -p "test_*.py"

What is tested here is the part that decides whether somebody is told. A collector that quietly
reports nothing looks exactly like a quiet week, which is the failure this whole script exists to
prevent -- so the "nothing new" path is tested as carefully as the alerting one.

The watermark gets most of the attention. It is the only stateful thing in the script, and both of
its failure modes are bad in different ways: too eager and every run resends the history until the
operator filters the alerts away; too greedy and a real signal is consumed and never seen again.
"""

import json
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

from security_signals import (
    MARKER,
    advance,
    collect,
    describe,
    is_new,
    parse_line,
    parse_timestamp,
    read_state,
    write_state,
)


def log_line(timestamp, kind="REPEATED_REFUSALS", count=23, actor="3f2a91c4-7d55-4e1a-9b2f-08c7d1e6a934",
             description="Sustained refusals.", marker=MARKER):
    """One line in the shape Serilog's JsonFormatter actually writes."""
    return json.dumps({
        "Timestamp": timestamp,
        "Level": "Warning",
        "MessageTemplate": "{Marker} {Kind}: {Description} (count {Count}, actor {ActorId})",
        "Properties": {
            "Marker": marker,
            "Kind": kind,
            "Description": description,
            "Count": count,
            "ActorId": actor,
        },
    })


class TimestampTests(unittest.TestCase):
    def test_given_serilogs_seven_fractional_digits_when_parsed_then_it_reads(self):
        # The reason this helper exists: fromisoformat takes three or six digits and Serilog writes
        # seven. Before the trim, every line in every log file was unparseable.
        parsed = parse_timestamp("2026-09-10T12:00:00.1234567+00:00")

        self.assertIsNotNone(parsed)
        self.assertEqual(2026, parsed.year)
        self.assertEqual(123456, parsed.microsecond)

    def test_given_six_digits_when_parsed_then_it_still_reads(self):
        self.assertIsNotNone(parse_timestamp("2026-09-10T12:00:00.123456+00:00"))

    def test_given_a_bare_date_when_parsed_then_it_reads_as_utc(self):
        parsed = parse_timestamp("2026-09-01")

        self.assertIsNotNone(parsed)
        self.assertEqual(timezone.utc, parsed.tzinfo)

    def test_given_a_naive_timestamp_when_parsed_then_it_is_made_aware(self):
        # Comparing a naive against an aware datetime raises, and it would raise inside the
        # alerting -- turning a signal into a crash.
        parsed = parse_timestamp("2026-09-10T12:00:00")

        self.assertIsNotNone(parsed.tzinfo)

    def test_given_nonsense_when_parsed_then_it_returns_none_rather_than_raising(self):
        self.assertIsNone(parse_timestamp("not a date"))
        self.assertIsNone(parse_timestamp(""))
        self.assertIsNone(parse_timestamp(None))


class ParseLineTests(unittest.TestCase):
    def test_given_a_signal_line_when_parsed_then_the_properties_are_read(self):
        signal = parse_line(log_line("2026-09-10T12:00:00.1234567+00:00"))

        self.assertIsNotNone(signal)
        self.assertEqual("REPEATED_REFUSALS", signal.kind)
        self.assertEqual(23, signal.count)
        self.assertEqual("3f2a91c4-7d55-4e1a-9b2f-08c7d1e6a934", signal.actor)

    def test_given_an_ordinary_line_when_parsed_then_nothing_is_reported(self):
        ordinary = json.dumps({
            "Timestamp": "2026-09-10T12:00:00.0000000+00:00",
            "Level": "Information",
            "MessageTemplate": "Hello world!",
            "Properties": {},
        })

        self.assertIsNone(parse_line(ordinary))

    def test_given_the_marker_only_in_a_users_own_text_then_nothing_is_reported(self):
        # Somebody signing up with the marker in their display name must not be able to raise an
        # alert. The marker is checked as a logged property, not as a substring of the line.
        impostor = json.dumps({
            "Timestamp": "2026-09-10T12:00:00.0000000+00:00",
            "Level": "Information",
            "MessageTemplate": "Created {Name}",
            "Properties": {"Name": f"{MARKER} LOCKOUT_SPIKE: everything is fine"},
        })

        self.assertIsNone(parse_line(impostor))

    def test_given_a_non_json_line_carrying_the_marker_then_it_still_reports(self):
        # A sink change must not silently switch detection off. An unparseable line that carries
        # the marker is reported verbatim rather than dropped.
        signal = parse_line(f"12:00:00 WRN {MARKER} LOCKOUT_SPIKE: 14 accounts locked")

        self.assertIsNotNone(signal)
        self.assertIn("LOCKOUT_SPIKE", signal.raw)

    def test_given_a_truncated_line_when_parsed_then_it_does_not_raise(self):
        # The API is appending to the file this reads. A half-written tail line is normal.
        signal = parse_line('{"Timestamp":"2026-09-10T12:00:00.0000000+00:00","Prop' + MARKER)

        self.assertIsNotNone(signal)


class WatermarkTests(unittest.TestCase):
    def setUp(self):
        self.instant = datetime(2026, 9, 10, 12, 0, 0, tzinfo=timezone.utc)
        self.signal = parse_line(log_line("2026-09-10T12:00:00.0000000+00:00"))

    def test_given_no_watermark_then_everything_is_new(self):
        self.assertTrue(is_new(self.signal, None, set()))

    def test_given_a_later_watermark_then_the_signal_is_not_new(self):
        self.assertFalse(is_new(self.signal, self.instant + timedelta(minutes=1), set()))

    def test_given_an_earlier_watermark_then_the_signal_is_new(self):
        self.assertTrue(is_new(self.signal, self.instant - timedelta(minutes=1), set()))

    def test_given_the_same_instant_and_a_recorded_digest_then_it_is_not_new(self):
        self.assertFalse(is_new(self.signal, self.instant, {self.signal.digest}))

    def test_given_the_same_instant_and_a_different_digest_then_it_is_new(self):
        # Two signals raised in one monitoring tick share a timestamp exactly. Reporting one and
        # dropping the other is the failure the digests exist to prevent.
        other = parse_line(log_line("2026-09-10T12:00:00.0000000+00:00", kind="LOCKOUT_SPIKE"))

        self.assertNotEqual(self.signal.digest, other.digest)
        self.assertTrue(is_new(other, self.instant, {self.signal.digest}))

    def test_given_a_signal_with_no_timestamp_then_it_always_reports(self):
        unstamped = parse_line(f"{MARKER} LOCKOUT_SPIKE: something")

        self.assertTrue(is_new(unstamped, self.instant + timedelta(days=1), set()))

    def test_given_two_signals_at_one_instant_when_advancing_then_both_digests_are_kept(self):
        other = parse_line(log_line("2026-09-10T12:00:00.0000000+00:00", kind="LOCKOUT_SPIKE"))

        watermark, digests = advance([self.signal, other], None, set())

        self.assertEqual(self.instant, watermark)
        self.assertEqual({self.signal.digest, other.digest}, digests)

    def test_given_a_newer_signal_when_advancing_then_stale_digests_are_dropped(self):
        # The digests only tie-break within one instant. Carrying them past it would grow the state
        # file without bound.
        later = parse_line(log_line("2026-09-10T13:00:00.0000000+00:00"))

        watermark, digests = advance([later], self.instant, {self.signal.digest})

        self.assertEqual(self.instant + timedelta(hours=1), watermark)
        self.assertEqual({later.digest}, digests)

    def test_given_only_older_signals_when_advancing_then_the_watermark_does_not_move_back(self):
        later = self.instant + timedelta(days=1)

        watermark, digests = advance([self.signal], later, {"kept"})

        self.assertEqual(later, watermark)
        self.assertEqual({"kept"}, digests)

    def test_given_nothing_reported_when_advancing_then_the_watermark_is_unchanged(self):
        watermark, digests = advance([], self.instant, {"kept"})

        self.assertEqual(self.instant, watermark)
        self.assertEqual({"kept"}, digests)


class StateFileTests(unittest.TestCase):
    def test_given_a_written_state_when_read_back_then_it_round_trips(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state"
            instant = datetime(2026, 9, 10, 12, 0, tzinfo=timezone.utc)

            write_state(path, instant, {"aaaa", "bbbb"})
            watermark, digests = read_state(path)

            self.assertEqual(instant, watermark)
            self.assertEqual({"aaaa", "bbbb"}, digests)

    def test_given_no_state_file_then_it_reads_as_a_first_run(self):
        with tempfile.TemporaryDirectory() as directory:
            watermark, digests = read_state(Path(directory) / "absent")

            self.assertIsNone(watermark)
            self.assertEqual(set(), digests)

    def test_given_a_corrupt_state_file_then_it_reads_as_a_first_run(self):
        # Reporting the history again is noisy. Reporting nothing because a file got truncated is
        # detection silently switching itself off, so corruption fails towards noise.
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state"
            path.write_text("{ not json", encoding="utf-8")

            watermark, digests = read_state(path)

            self.assertIsNone(watermark)
            self.assertEqual(set(), digests)

    def test_given_a_write_when_it_completes_then_no_temporary_file_is_left(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state"
            write_state(path, datetime.now(timezone.utc), {"aaaa"})

            self.assertEqual(["state"], [entry.name for entry in Path(directory).iterdir()])


class CollectTests(unittest.TestCase):
    def _directory(self, files):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)

        for name, lines in files.items():
            (Path(directory.name) / name).write_text("\n".join(lines) + "\n", encoding="utf-8")

        return directory.name

    def test_given_signals_across_rolled_files_then_all_are_collected(self):
        directory = self._directory({
            "log-20260909.json": [log_line("2026-09-09T12:00:00.0000000+00:00")],
            "log-20260910.json": [log_line("2026-09-10T12:00:00.0000000+00:00", kind="LOCKOUT_SPIKE")],
        })

        signals = collect(directory)

        self.assertEqual(2, len(signals))
        self.assertEqual(["REPEATED_REFUSALS", "LOCKOUT_SPIKE"], [s.kind for s in signals])

    def test_given_a_watermark_then_only_later_signals_are_collected(self):
        directory = self._directory({
            "log-20260910.json": [
                log_line("2026-09-10T11:00:00.0000000+00:00"),
                log_line("2026-09-10T13:00:00.0000000+00:00", kind="LOCKOUT_SPIKE"),
            ],
        })

        signals = collect(directory, datetime(2026, 9, 10, 12, 0, tzinfo=timezone.utc))

        self.assertEqual(1, len(signals))
        self.assertEqual("LOCKOUT_SPIKE", signals[0].kind)

    def test_given_since_then_earlier_signals_are_excluded(self):
        directory = self._directory({
            "log-20260910.json": [
                log_line("2026-09-01T12:00:00.0000000+00:00"),
                log_line("2026-09-10T12:00:00.0000000+00:00", kind="LOCKOUT_SPIKE"),
            ],
        })

        signals = collect(directory, since=datetime(2026, 9, 5, tzinfo=timezone.utc))

        self.assertEqual(["LOCKOUT_SPIKE"], [s.kind for s in signals])

    def test_given_a_quiet_log_then_nothing_is_collected(self):
        directory = self._directory({
            "log-20260910.json": [json.dumps({
                "Timestamp": "2026-09-10T12:00:00.0000000+00:00",
                "Level": "Information",
                "MessageTemplate": "Ready to run!",
                "Properties": {},
            })],
        })

        self.assertEqual([], collect(directory))

    def test_given_a_file_that_is_not_a_log_then_it_is_not_read(self):
        # The state file lives in the same directory by default, and it must not be scanned as
        # though it were a log.
        directory = self._directory({
            "log-20260910.json": [log_line("2026-09-10T12:00:00.0000000+00:00")],
            "notes.txt": [log_line("2026-09-10T13:00:00.0000000+00:00", kind="LOCKOUT_SPIKE")],
        })

        self.assertEqual(["REPEATED_REFUSALS"], [s.kind for s in collect(directory)])

    def test_given_a_missing_directory_then_it_says_so_rather_than_reporting_nothing(self):
        from security_signals import CollectionError

        with self.assertRaises(CollectionError):
            collect("/nonexistent/heimdall/logs")


class DescribeTests(unittest.TestCase):
    def test_given_signals_then_the_report_counts_them_by_kind(self):
        signals = [
            parse_line(log_line("2026-09-10T12:00:00.0000000+00:00")),
            parse_line(log_line("2026-09-10T12:01:00.0000000+00:00")),
            parse_line(log_line("2026-09-10T12:02:00.0000000+00:00", kind="LOCKOUT_SPIKE")),
        ]

        report = describe(signals)

        self.assertIn("3 new security signal(s)", report)
        self.assertIn("LOCKOUT_SPIKE x1", report)
        self.assertIn("REPEATED_REFUSALS x2", report)

    def test_given_signals_then_the_report_says_when_awareness_happened(self):
        # Both notification clocks run from awareness, and this message is often the moment it
        # happens. Saying so in the alert is what makes the register entry accurate.
        report = describe([parse_line(log_line("2026-09-10T12:00:00.0000000+00:00"))])

        self.assertIn("awareness", report)

    def test_given_no_signals_then_the_report_says_nothing_happened(self):
        self.assertEqual("No new security signals.", describe([]))


if __name__ == "__main__":
    unittest.main()
