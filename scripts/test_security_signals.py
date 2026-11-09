"""Unit tests for the collector in security_signals.py.

Run from the repository root:  python -m unittest discover -s scripts -p "test_*.py"

What is tested here is the part that decides whether somebody is told. A collector that quietly
reports nothing looks exactly like a quiet week, which is the failure this whole script exists to
prevent -- so the "nothing new" path is tested as carefully as the alerting one.

The watermark gets most of the attention. It is the only stateful thing in the script, and both of
its failure modes are bad in different ways: too eager and every run resends the history until the
operator filters the alerts away; too greedy and a real signal is consumed and never seen again.
"""

import contextlib
import http.client
import io
import json
import os
import tempfile
import unittest
import urllib.error
from datetime import datetime, timedelta, timezone
from email.message import Message
from pathlib import Path
from unittest import mock

from security_signals import (
    HERMES_BODY_LIMIT,
    MARKER,
    CollectionError,
    advance,
    collect,
    describe,
    is_new,
    main,
    parse_line,
    parse_timestamp,
    read_state,
    send,
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

    def test_given_room_for_everything_then_a_limit_changes_nothing(self):
        signals = [parse_line(log_line("2026-09-10T12:00:00.0000000+00:00"))]

        self.assertEqual(describe(signals), describe(signals, limit=HERMES_BODY_LIMIT))


def many_signals(number):
    start = datetime(2026, 9, 10, 12, 0, tzinfo=timezone.utc)

    return [
        parse_line(log_line((start + timedelta(minutes=index)).isoformat(), count=index))
        for index in range(number)
    ]


def utf16_length(text):
    """How Hermes measures a body: C#'s string.Length counts UTF-16 code units."""
    return len(text.encode("utf-16-le", "surrogatepass")) // 2


class DescribeWithinALimitTests(unittest.TestCase):
    def test_given_too_many_signals_then_the_report_fits_hermess_limit(self):
        report = describe(many_signals(500), limit=HERMES_BODY_LIMIT)

        self.assertLessEqual(utf16_length(report), HERMES_BODY_LIMIT)

    def test_given_too_many_signals_then_every_one_is_still_counted(self):
        report = describe(many_signals(500), limit=HERMES_BODY_LIMIT)

        self.assertIn("500 new security signal(s)", report)
        self.assertIn("REPEATED_REFUSALS x500", report)

    def test_given_too_many_signals_then_the_newest_are_the_ones_listed(self):
        report = describe(many_signals(500), limit=HERMES_BODY_LIMIT)

        self.assertIn("count: 499", report)
        self.assertNotIn("count: 0\n", report)

    def test_given_too_many_signals_then_it_says_how_to_list_the_rest(self):
        # The watermark moves once this is delivered, so a bare --stdout would find nothing: the
        # command has to ignore the watermark and start at the oldest signal left out.
        report = describe(many_signals(500), limit=HERMES_BODY_LIMIT)

        self.assertRegex(report, r"\d+ older signal\(s\) are counted above but not listed")
        self.assertIn("--stdout --all --since 2026-09-10T12:00:00+00:00", report)

    def test_given_too_many_signals_then_the_report_still_says_when_awareness_happened(self):
        self.assertIn("awareness", describe(many_signals(500), limit=HERMES_BODY_LIMIT))

    def test_given_one_enormous_line_then_it_is_clipped_rather_than_refused(self):
        huge = parse_line(log_line("2026-09-10T12:00:00.0000000+00:00", description="x" * 50_000))

        report = describe([huge], limit=HERMES_BODY_LIMIT)

        self.assertLessEqual(utf16_length(report), HERMES_BODY_LIMIT)
        self.assertIn("1 new security signal(s)", report)

    def test_given_characters_outside_the_bmp_then_they_count_as_hermes_counts_them(self):
        # One emoji is one Python character and two C# ones. Counting Python's way would send a
        # body Hermes answers 400 to, and the alert would never leave.
        wide = [
            parse_line(log_line(f"2026-09-10T12:{minute:02d}:00+00:00", description="\U0001F525" * 400))
            for minute in range(60)
        ]

        self.assertLessEqual(utf16_length(describe(wide, limit=HERMES_BODY_LIMIT)), HERMES_BODY_LIMIT)

    def test_given_only_unplaceable_lines_left_out_then_the_command_has_no_since(self):
        lines = [parse_line(f"{MARKER} not json {index} " + "y" * 900) for index in range(40)]

        report = describe(lines, limit=HERMES_BODY_LIMIT)

        self.assertIn("--stdout --all", report)
        self.assertNotIn("--since", report)


class FakeResponse:
    def __init__(self, status=202):
        self.status = status

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False


def http_error(code, headers=None, body=b"refused"):
    message = Message()

    for name, value in (headers or {}).items():
        message[name] = value

    return urllib.error.HTTPError("http://hermes", code, "refused", message, io.BytesIO(body))


HERMES_ENVIRONMENT = {
    "HERMES_BASE_URL": "http://hermes-api.production:8080",
    "HEIMDALL_SIGNAL_HERMES_API_KEY": "signals-key",
    "HEIMDALL_SIGNAL_ALERT_TO": "owner@example.com",
}


class SendTests(unittest.TestCase):
    def setUp(self):
        patcher = mock.patch.dict(os.environ, HERMES_ENVIRONMENT, clear=True)
        patcher.start()
        self.addCleanup(patcher.stop)

    @mock.patch("security_signals.urllib.request.urlopen", return_value=FakeResponse())
    def test_given_a_202_then_it_posts_to_hermes_with_this_jobs_key(self, urlopen):
        send("subject", "body", "owner@example.com")

        request = urlopen.call_args.args[0]

        self.assertEqual("http://hermes-api.production:8080/api/emails", request.full_url)
        self.assertEqual("POST", request.get_method())
        self.assertEqual("Bearer signals-key", request.get_header("Authorization"))
        self.assertEqual("application/json", request.get_header("Content-type"))

    @mock.patch("security_signals.urllib.request.urlopen", return_value=FakeResponse())
    def test_given_a_message_then_it_never_names_a_sender(self, urlopen):
        # Hermes fixes the sender per client and does not accept one in a request (FR-PO-02).
        send("subject", "body", "owner@example.com")

        self.assertEqual(
            {"to": "owner@example.com", "subject": "subject", "body": "body"},
            json.loads(urlopen.call_args.args[0].data))

    @mock.patch("security_signals.urllib.request.urlopen", return_value=FakeResponse())
    def test_given_a_trailing_slash_then_the_path_stays_single(self, urlopen):
        os.environ["HERMES_BASE_URL"] = "http://hermes-api.production:8080/"

        send("subject", "body", "owner@example.com")

        self.assertEqual("http://hermes-api.production:8080/api/emails", urlopen.call_args.args[0].full_url)

    @mock.patch("security_signals.urllib.request.urlopen", return_value=FakeResponse(200))
    def test_given_a_success_that_is_not_202_then_it_is_not_delivery(self, _):
        # 202 is the one answer that means Mailgun took the message.
        with self.assertRaises(CollectionError):
            send("subject", "body", "owner@example.com")

    def test_given_a_429_then_the_error_says_when_to_try_again(self):
        with mock.patch("security_signals.urllib.request.urlopen",
                        side_effect=http_error(429, {"Retry-After": "120"})):
            with self.assertRaisesRegex(CollectionError, "429.*120"):
                send("subject", "body", "owner@example.com")

    def test_given_any_refusal_then_it_is_a_delivery_failure(self):
        for code in (400, 401, 403, 413, 502):
            with self.subTest(code=code), mock.patch(
                    "security_signals.urllib.request.urlopen", side_effect=http_error(code)):
                with self.assertRaisesRegex(CollectionError, str(code)):
                    send("subject", "body", "owner@example.com")

    def test_given_hermes_cannot_be_reached_then_it_is_a_delivery_failure(self):
        # A read timeout and a dropped connection are not URLError: uncaught, they would end the
        # run without printing the signals it was holding.
        failures = [
            urllib.error.URLError("no route"),
            TimeoutError("timed out"),
            http.client.RemoteDisconnected("closed"),
        ]

        for failure in failures:
            with self.subTest(failure=failure), mock.patch(
                    "security_signals.urllib.request.urlopen", side_effect=failure):
                with self.assertRaises(CollectionError):
                    send("subject", "body", "owner@example.com")

    def test_given_only_heimdalls_own_key_then_it_refuses_to_borrow_it(self):
        # Heimdall's client shares its limits with Heimdall's mail, so a flood could refuse the
        # alert it caused. Falling back to that key would bring the problem back silently.
        del os.environ["HEIMDALL_SIGNAL_HERMES_API_KEY"]
        os.environ["HERMES_API_KEY"] = "heimdalls-key"

        with self.assertRaisesRegex(CollectionError, "HEIMDALL_SIGNAL_HERMES_API_KEY"):
            send("subject", "body", "owner@example.com")

    def test_given_no_base_url_then_it_says_so(self):
        del os.environ["HERMES_BASE_URL"]

        with self.assertRaisesRegex(CollectionError, "HERMES_BASE_URL"):
            send("subject", "body", "owner@example.com")

    def test_given_an_address_that_cannot_be_called_then_it_says_so(self):
        for address in ("hermes-api.production:8080", "ftp://hermes", "http://["):
            os.environ["HERMES_BASE_URL"] = address

            with self.subTest(address=address), self.assertRaisesRegex(CollectionError, "HERMES_BASE_URL"):
                send("subject", "body", "owner@example.com")


class RunTests(unittest.TestCase):
    """The whole run: the two promises the DPIA's rating of R-08 rests on."""

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.state = self.directory / ".security-signals-state"

        patcher = mock.patch.dict(os.environ, HERMES_ENVIRONMENT, clear=True)
        patcher.start()
        self.addCleanup(patcher.stop)

    def _logs(self, lines):
        (self.directory / "log-20260910.json").write_text("\n".join(lines) + "\n", encoding="utf-8")

    def _run(self, *arguments):
        stdout, stderr = io.StringIO(), io.StringIO()

        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            code = main(["--log-directory", str(self.directory), "--quiet", *arguments])

        return code, stdout.getvalue(), stderr.getvalue()

    def _many_logs(self):
        self._logs([log_line(s.timestamp.isoformat(), count=s.count) for s in many_signals(500)])

    def test_given_a_delivered_alert_then_it_exits_zero_and_is_not_sent_twice(self):
        self._logs([log_line("2026-09-10T12:00:00.0000000+00:00")])

        with mock.patch("security_signals.urllib.request.urlopen", return_value=FakeResponse()) as urlopen:
            first = self._run()
            second = self._run()

        self.assertEqual((0, 0), (first[0], second[0]))
        self.assertEqual(1, urlopen.call_count)

    def test_given_any_delivery_failure_then_it_exits_one_and_keeps_the_watermark(self):
        self._logs([log_line("2026-09-10T12:00:00.0000000+00:00")])

        failures = [http_error(429, {"Retry-After": "60"}), http_error(403), http_error(502),
                    urllib.error.URLError("no route"), TimeoutError("timed out")]

        for failure in failures:
            with self.subTest(failure=failure), mock.patch(
                    "security_signals.urllib.request.urlopen", side_effect=failure):
                code, _, stderr = self._run()

                self.assertEqual(1, code)
                self.assertIn("REPEATED_REFUSALS", stderr)
                self.assertFalse(self.state.exists())

    def test_given_a_failure_then_the_next_run_still_delivers_it(self):
        self._logs([log_line("2026-09-10T12:00:00.0000000+00:00")])

        with mock.patch("security_signals.urllib.request.urlopen", side_effect=http_error(429)):
            self.assertEqual(1, self._run()[0])

        with mock.patch("security_signals.urllib.request.urlopen", return_value=FakeResponse()) as urlopen:
            self.assertEqual(0, self._run()[0])

        self.assertEqual(1, urlopen.call_count)
        self.assertTrue(self.state.exists())

    def test_given_too_many_signals_then_one_message_goes_and_all_are_consumed(self):
        self._many_logs()

        with mock.patch("security_signals.urllib.request.urlopen", return_value=FakeResponse()) as urlopen:
            first = self._run()
            second = self._run()

        self.assertEqual((0, 0), (first[0], second[0]))
        self.assertEqual(1, urlopen.call_count)
        self.assertLessEqual(
            utf16_length(json.loads(urlopen.call_args.args[0].data)["body"]), HERMES_BODY_LIMIT)

    def test_given_a_failure_with_too_many_signals_then_stderr_holds_all_of_them(self):
        self._many_logs()

        with mock.patch("security_signals.urllib.request.urlopen", side_effect=http_error(502)):
            _, _, stderr = self._run()

        self.assertIn("count: 0\n", stderr)
        self.assertIn("count: 499\n", stderr)

    def test_given_stdout_then_nothing_is_capped_or_sent(self):
        self._many_logs()

        with mock.patch("security_signals.urllib.request.urlopen") as urlopen:
            code, stdout, _ = self._run("--stdout")

        self.assertEqual(0, code)
        self.assertIn("count: 0\n", stdout)
        self.assertEqual(0, urlopen.call_count)
        self.assertFalse(self.state.exists())

    def test_given_no_settings_then_signals_found_are_a_delivery_failure(self):
        self._logs([log_line("2026-09-10T12:00:00.0000000+00:00")])
        del os.environ["HEIMDALL_SIGNAL_HERMES_API_KEY"]

        code, _, stderr = self._run()

        self.assertEqual(1, code)
        self.assertIn("REPEATED_REFUSALS", stderr)
        self.assertFalse(self.state.exists())

    def test_given_a_missing_log_directory_then_it_exits_two(self):
        stderr = io.StringIO()

        with contextlib.redirect_stderr(stderr):
            code = main(["--log-directory", str(self.directory / "absent"), "--quiet"])

        self.assertEqual(2, code)


if __name__ == "__main__":
    unittest.main()
