#!/usr/bin/env python3
"""Collect SECURITY_SIGNAL lines from the logs and e-mail whatever is new.

`SecurityMonitoringService` writes a warning-level line for every signal that crosses a threshold,
and until this script existed nothing read them. That gap was not cosmetic: **both breach
notification clocks -- LGPD Art. 48 and GDPR Art. 33 -- run from awareness**, and a signal nobody
receives has not been detected. See NFR-26, the Incident Response Document Sec. 6, and residual risk
R-08 in the DPIA.

Run it from cron. It reads the rolling log files, finds the lines it has not reported before, and
sends them to one address through the same Mailgun account the API already uses.

Usage (from anywhere in the repository):

    python scripts/security_signals.py                     # report new signals by e-mail
    python scripts/security_signals.py --stdout            # print them instead of sending
    python scripts/security_signals.py --stdout --all      # ignore the watermark, print everything
    python scripts/security_signals.py --since 2026-09-01  # only signals at or after a date

Configuration, all environment variables:

    HEIMDALL_LOG_DIRECTORY      where the API writes log-*.json  (same variable the API reads)
    HEIMDALL_SIGNAL_ALERT_TO    the address alerts go to         (required unless --stdout)
    HEIMDALL_SIGNAL_ALERT_FROM  the sender  (default: heimdall-signals@$MAILGUN_DOMAIN)
    HEIMDALL_SIGNAL_STATE_FILE  the watermark  (default: <log directory>/.security-signals-state)
    MAILGUN_API_KEY             the API's own Mailgun credentials, unchanged
    MAILGUN_DOMAIN
    MAILGUN_API_VERSION         default v3
    MAILGUN_API_BASE_URL        default https://api.mailgun.net -- set the EU endpoint here if the
                                Mailgun account is in the EU region

Exit codes: 0 ran and delivered whatever it found, 1 signals were found and could NOT be delivered,
2 the collection itself could not run.

The non-zero exit on a delivery failure is the point of the exit codes. This script is the thing
that makes somebody aware; if it fails silently the failure is invisible in exactly the way the
signals themselves were. Give cron a MAILTO, or wrap the call, so a broken collector is itself
noticed.

**What gets sent.** Only the marker, the kind, the count, the timestamp and the actor's PublicId --
the fields the service logs. A PublicId is a pseudonym: it identifies an account to somebody holding
the database and to nobody else, which is what keeps this within the same processing already
described in the ROPA. Do not extend this to include e-mail addresses; NFR-22 keeps them out of the
logs in the first place, and LogSafeEmail exists for the cases that need a reference.

**The watermark is what makes this readable.** Without it every run would resend the whole history,
and an alert that repeats everything it has ever said is one that gets filtered into a folder within
a week. Signals repeat on every monitoring tick while a condition persists -- that is deliberate in
the service -- so a run reports each repetition once, and no more.
"""

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from base64 import b64encode
from datetime import datetime, timezone
from hashlib import sha256
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

# Must match SecurityMonitoringService.Marker. There is a test in the .NET suite that fails if the
# constant moves, so this string cannot drift without something going red.
MARKER = "SECURITY_SIGNAL"

DEFAULT_LOG_DIRECTORY = "logs"
LOG_FILE_GLOB = "log-*.json"
STATE_FILE_NAME = ".security-signals-state"

DEFAULT_MAILGUN_BASE_URL = "https://api.mailgun.net"
DEFAULT_MAILGUN_VERSION = "v3"

# Serilog writes seven fractional digits; datetime.fromisoformat accepts three or six and rejects
# the rest, so the tail is trimmed rather than the whole line being dropped as unparseable.
_FRACTION = re.compile(r"(\.\d{6})\d+")


class CollectionError(Exception):
    """The collection could not run -- a missing directory, an unreadable state file."""


class Signal:
    """One signal line, as it will be reported."""

    def __init__(self, timestamp, kind, description, count, actor, raw):
        self.timestamp = timestamp
        self.kind = kind
        self.description = description
        self.count = count
        self.actor = actor
        self.raw = raw

    @property
    def digest(self):
        """Identifies this line within its own timestamp, for the watermark's tie-breaking."""
        return sha256(self.raw.encode("utf-8", "replace")).hexdigest()[:16]

    def __repr__(self):
        stamp = self.timestamp.isoformat() if self.timestamp else "(no timestamp)"
        return f"{stamp}  {self.kind}  count={self.count}  actor={self.actor}"


def parse_timestamp(value):
    """Read one Serilog timestamp. Returns None rather than raising: a line with an unreadable
    timestamp is still a signal worth reporting, and losing it would be the wrong trade."""
    if not value:
        return None

    try:
        parsed = datetime.fromisoformat(_FRACTION.sub(r"\1", value.strip()))
    except (ValueError, AttributeError):
        return None

    # A naive timestamp is treated as UTC so that every comparison against the watermark is between
    # two aware values. Mixing the two raises, and it would raise inside the alerting.
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=timezone.utc)


def parse_line(line):
    """Turn one log line into a Signal, or None if it carries no signal.

    Handles both shapes deliberately. The API writes Serilog's JsonFormatter, so the properties are
    structured and are read as such -- matching the prose would break the moment somebody improves
    the wording. But a line that is not JSON at all, from a console capture or a future sink, still
    reports if it contains the marker. Dropping it would mean a formatting change could silently
    switch detection off."""
    if MARKER not in line:
        return None

    raw = line.strip()

    try:
        entry = json.loads(raw)
    except (json.JSONDecodeError, TypeError):
        return Signal(None, "(unparsed)", raw, None, None, raw)

    if not isinstance(entry, dict):
        return Signal(None, "(unparsed)", raw, None, None, raw)

    properties = entry.get("Properties") or {}

    if not isinstance(properties, dict):
        properties = {}

    # Confirm the marker is the logged property rather than a substring of somebody's message.
    # Without this a user-supplied value containing the marker could raise a false alert.
    if properties.get("Marker") != MARKER:
        return None

    return Signal(
        parse_timestamp(entry.get("Timestamp")),
        properties.get("Kind") or "(no kind)",
        properties.get("Description") or "",
        properties.get("Count"),
        properties.get("ActorId"),
        raw,
    )


def read_state(path):
    """Return (watermark, digests-seen-at-that-instant). Missing or corrupt state means a first
    run: report everything rather than nothing, because an unreadable watermark must not be a
    silent way to stop alerting."""
    try:
        state = json.loads(Path(path).read_text(encoding="utf-8"))
    except FileNotFoundError:
        return None, set()
    except (OSError, json.JSONDecodeError):
        return None, set()

    if not isinstance(state, dict):
        return None, set()

    return parse_timestamp(state.get("watermark")), set(state.get("digests") or [])


def write_state(path, watermark, digests):
    payload = {
        "watermark": watermark.isoformat() if watermark else None,
        "digests": sorted(digests),
        "written": datetime.now(timezone.utc).isoformat(),
    }

    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)

    # Written to a temporary file and moved, so an interrupted run cannot leave a truncated
    # watermark behind -- which would resend the whole history on the next run.
    temporary = destination.with_suffix(destination.suffix + ".tmp")
    temporary.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    temporary.replace(destination)


def advance(signals, watermark, digests):
    """Return the watermark and tie-breaking digests after reporting these signals.

    Two signals can share a timestamp -- the monitoring pass raises its findings in one tick. A
    watermark alone would then either resend one of them forever or drop it, so the digests of every
    signal at the newest instant are carried alongside it."""
    stamped = [signal for signal in signals if signal.timestamp]

    if not stamped:
        return watermark, digests

    newest = max(signal.timestamp for signal in stamped)

    if watermark and newest < watermark:
        return watermark, digests

    at_newest = {signal.digest for signal in stamped if signal.timestamp == newest}

    if watermark and newest == watermark:
        return watermark, digests | at_newest

    return newest, at_newest


def is_new(signal, watermark, digests):
    """A signal with no readable timestamp always reports: it cannot be placed against the
    watermark, and reporting it twice is a smaller failure than never reporting it."""
    if signal.timestamp is None:
        return True

    if watermark is None:
        return True

    if signal.timestamp > watermark:
        return True

    return signal.timestamp == watermark and signal.digest not in digests


def collect(log_directory, watermark=None, digests=frozenset(), since=None):
    """Read every log file and return the signals that have not been reported."""
    directory = Path(log_directory)

    if not directory.is_dir():
        raise CollectionError(
            f"No log directory at {directory}. Set HEIMDALL_LOG_DIRECTORY to where the API writes."
        )

    found = []

    for path in sorted(directory.glob(LOG_FILE_GLOB)):
        try:
            # errors="replace" because a partially written line at the tail of the file the API is
            # currently appending to must not abort the whole run.
            with path.open("r", encoding="utf-8", errors="replace") as handle:
                for line in handle:
                    signal = parse_line(line)

                    if signal is None:
                        continue

                    if since and signal.timestamp and signal.timestamp < since:
                        continue

                    if not is_new(signal, watermark, digests):
                        continue

                    found.append(signal)
        except OSError as error:
            raise CollectionError(f"Could not read {path}: {error}") from error

    found.sort(key=lambda signal: (signal.timestamp is not None, signal.timestamp or datetime.min.replace(tzinfo=timezone.utc)))

    return found


def describe(signals):
    """The body of the alert. Written to be read at three in the morning: the counts first, then
    the lines, then what to do about it."""
    if not signals:
        return "No new security signals."

    by_kind = {}

    for signal in signals:
        by_kind[signal.kind] = by_kind.get(signal.kind, 0) + 1

    summary = ", ".join(f"{kind} x{count}" for kind, count in sorted(by_kind.items()))

    lines = [
        f"{len(signals)} new security signal(s): {summary}",
        "",
        "A signal is not a breach. It is a reason to look.",
        "",
    ]

    for signal in signals:
        stamp = signal.timestamp.isoformat() if signal.timestamp else "(no timestamp)"
        lines.append(f"  {stamp}  {signal.kind}")

        if signal.count is not None:
            lines.append(f"      count: {signal.count}")

        if signal.actor:
            lines.append(f"      actor: {signal.actor}")

        if signal.description:
            lines.append(f"      {signal.description}")

        lines.append("")

    lines.extend([
        "The actor is a PublicId, not a name or an address.",
        "",
        "What follows a signal is the Incident Response Document. If this turns out to be a",
        "breach, note the time you read this message: both notification clocks run from",
        "awareness, and this is when awareness happened.",
    ])

    return "\n".join(lines)


def send(subject, body, recipient, sender):
    """Send through Mailgun with the API's own credentials. Raises on any failure -- the caller
    turns that into a non-zero exit, because a delivery failure here is indistinguishable in effect
    from having no detection at all."""
    key = os.environ.get("MAILGUN_API_KEY")
    domain = os.environ.get("MAILGUN_DOMAIN")

    if not key or not domain:
        raise CollectionError(
            "MAILGUN_API_KEY and MAILGUN_DOMAIN must be set to send alerts. Use --stdout to print."
        )

    base = os.environ.get("MAILGUN_API_BASE_URL") or DEFAULT_MAILGUN_BASE_URL
    version = os.environ.get("MAILGUN_API_VERSION") or DEFAULT_MAILGUN_VERSION
    url = f"{base.rstrip('/')}/{version}/{domain}/messages"

    payload = urllib.parse.urlencode({
        "from": sender or f"heimdall-signals@{domain}",
        "to": recipient,
        "subject": subject,
        "text": body,
    }).encode("utf-8")

    request = urllib.request.Request(url, data=payload, method="POST")
    credentials = b64encode(f"api:{key}".encode("utf-8")).decode("ascii")
    request.add_header("Authorization", f"Basic {credentials}")

    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            if response.status >= 300:
                raise CollectionError(f"Mailgun answered {response.status}")
    except urllib.error.HTTPError as error:
        detail = error.read().decode("utf-8", "replace")[:300]
        raise CollectionError(f"Mailgun refused the alert ({error.code}): {detail}") from error
    except urllib.error.URLError as error:
        raise CollectionError(f"Could not reach Mailgun: {error.reason}") from error


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--log-directory",
        default=os.environ.get("HEIMDALL_LOG_DIRECTORY") or DEFAULT_LOG_DIRECTORY,
        help="where the API writes log-*.json (default: $HEIMDALL_LOG_DIRECTORY, else ./logs)")
    parser.add_argument(
        "--stdout", action="store_true",
        help="print the report instead of e-mailing it, and leave the watermark alone")
    parser.add_argument(
        "--all", action="store_true",
        help="ignore the watermark and report every signal in the logs")
    parser.add_argument(
        "--since", default=None,
        help="only report signals at or after this ISO-8601 instant or date")
    parser.add_argument(
        "--state-file", default=os.environ.get("HEIMDALL_SIGNAL_STATE_FILE"),
        help="the watermark (default: <log directory>/" + STATE_FILE_NAME + ")")
    parser.add_argument(
        "--quiet", action="store_true",
        help="say nothing when there were no new signals")

    arguments = parser.parse_args(argv)

    state_file = arguments.state_file or Path(arguments.log_directory) / STATE_FILE_NAME

    since = None

    if arguments.since:
        since = parse_timestamp(arguments.since)

        if since is None:
            print(f"Could not read --since {arguments.since!r} as a date.", file=sys.stderr)
            return 2

    watermark, digests = (None, set()) if arguments.all else read_state(state_file)

    try:
        signals = collect(arguments.log_directory, watermark, digests, since)
    except CollectionError as error:
        print(f"{error}", file=sys.stderr)
        return 2

    if not signals:
        if not arguments.quiet:
            print("No new security signals.")
        return 0

    body = describe(signals)
    subject = f"[Heimdall] {len(signals)} new security signal(s)"

    if arguments.stdout:
        print(body)
        return 0

    recipient = os.environ.get("HEIMDALL_SIGNAL_ALERT_TO")

    if not recipient:
        print(
            "HEIMDALL_SIGNAL_ALERT_TO is not set, so these signals have nowhere to go:",
            file=sys.stderr)
        print(body, file=sys.stderr)
        return 1

    try:
        send(subject, body, recipient, os.environ.get("HEIMDALL_SIGNAL_ALERT_FROM"))
    except CollectionError as error:
        # Printed in full on the way out. If the mail path is broken, stderr -- which cron mails, or
        # the journal keeps -- is the only place left that the signals can still reach somebody.
        print(f"{error}", file=sys.stderr)
        print(body, file=sys.stderr)
        return 1

    # Only after delivery succeeded. Advancing first would mean a Mailgun outage silently consumed
    # the signals it failed to send.
    write_state(state_file, *advance(signals, watermark, digests))

    if not arguments.quiet:
        print(f"Reported {len(signals)} signal(s) to {recipient}.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
