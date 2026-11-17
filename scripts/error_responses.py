#!/usr/bin/env python3
"""Derive, from the source, the error statuses each operation's handler can answer.

The OpenAPI document lists every response an operation can give, and most error responses come from
the handler: it fails with a message, and the action passes its output to
`ToActionResult(statusMap: <X>MessageMap.StatusCodes)`, which answers the status the map gives the
first error (400 for one the map does not hold). Neither the messages a handler can fail with nor
the map an action passes are metadata the API can read at run time -- they are what the code does
-- so the document cannot work them out for itself. This script reads them from the source, the way
OpenApiResponseContractTests reads the success statuses, and writes them to
src/Presentation/ArturRios.Heimdall.WebApi/Documentation/error-responses.json. The Web API embeds
that file, and its ErrorResponseFilter documents each operation from it, so the running API's
/swagger document and docs/openapi/heimdall.json say the same thing.

For each action that dispatches an input through the mediator:

- the status map is the one its `statusMap:` argument names;
- the handler is `<Input>Handler.cs`, and what it can fail with is every message constant
  (`<X>Messages.<Name>`, `RelationalErrors.<Name>`) in the handler and in the code it reaches: the
  types it names, the classes implementing the interfaces among them, and the validator of any
  `IValidator<T>`, transitively, leaving out messages, inputs, outputs, entities and controllers;
- each such message the map gives a status of 400 or more is listed under that status;
- a handler that validates its input can also fail with a validator's own message, which no map
  holds: 400, listed as `IValidator`;
- a handler that writes through an `IAsyncRepository` and returns the write's errors can answer
  the repository's own failures, which DataAccessMessageMap maps (409 and 503);
- code that derives a password hash through `PasswordHashGate.Shared` can meet a saturated gate,
  which PasswordHashSaturationFilter answers with 503 and `Retry-After: 5`: listed as
  `PasswordHashGate`;
- a 429 from code that takes an `IRetryAfterAdvisor` may carry a `Retry-After` header: listed as
  `IRetryAfterAdvisor`.

It over-approximates on purpose: a message the code reaches is taken to be one it can answer with.
A status the document lists and the operation never answers costs a reader a line; one it answers
and the document leaves out fails a generated client.

Usage (from anywhere in the repository):

    python scripts/error_responses.py           # rewrite the catalog
    python scripts/error_responses.py --check   # fail if the committed catalog is out of date

scripts/openapi.py runs it before generating the document, and its --check checks it too.
"""

import argparse
import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
CATALOG = REPO_ROOT / "src/Presentation/ArturRios.Heimdall.WebApi/Documentation/error-responses.json"

VALIDATION = "IValidator"
PASSWORD_HASH = "PasswordHashGate"
RETRY_ADVISOR = "IRetryAfterAdvisor"
DATA_ACCESS = [
    "RelationalErrors.ConcurrencyMessage",
    "RelationalErrors.IntegrityViolationMessage",
    "RelationalErrors.TransientMessage",
    "RelationalErrors.UniqueViolationMessage",
]

STATUSES = {
    "Ok": 200, "Created": 201, "Accepted": 202, "BadRequest": 400, "Unauthorized": 401, "Forbidden": 403,
    "NotFound": 404, "Conflict": 409, "UnprocessableEntity": 422, "TooManyRequests": 429,
    "InternalServerError": 500, "ServiceUnavailable": 503,
}

# Folders whose types hold no logic that fails a request: following them would only add noise.
EXCLUDED_FOLDERS = ("ArturRios.Heimdall.Domain", "Messages", "Controllers", "Input", "Output", "Documentation", "Migrations", "EntityMaps")

STRING_LITERAL = re.compile(r'"((?:[^"\\]|\\.)*)"')
MESSAGE_REFERENCE = re.compile(r"\b(\w+Messages\.\w+|RelationalErrors\.\w+)\b")
DECLARATION = re.compile(r"\b(?:class|interface|record|struct)\s+(\w+)")
TYPE_HEADER = re.compile(r"\b(?:class|record)\s+\w+(?:<[^>]*>)?\s*(?:\((?:[^()]|\([^()]*\))*\))?\s*:\s*([^{;]+)")
IDENTIFIER = re.compile(r"\b([A-Z]\w*)\b")
VALIDATED = re.compile(r"IValidator<\s*(\w+)\s*>")
VALIDATOR_OF = re.compile(r"AbstractValidator<\s*(\w+)\s*>")
MEDIATOR_INPUT = re.compile(r"Execute\w*Async<\s*(\w+)\s*,")
STATUS_MAP_ARGUMENT = re.compile(r"statusMap:\s*(\w+)\.StatusCodes")
MAP_ENTRY = re.compile(r"\[\s*([\w.]+)\s*\]\s*=\s*HttpStatusCodes\.(\w+)")
CONSTANT = re.compile(r"public\s+const\s+string\s+(\w+)\s*=([^;]*);", re.S)
METHOD_NAME = re.compile(r"(\w+)\(")


def strip_comments(text):
    """Code only: a type or message named in a comment is not one the code uses."""
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)

    return re.sub(r"//[^\n]*", "", text)


class Source:
    """The source files under src/, read once, with what the derivation needs indexed."""

    def __init__(self, root):
        self.root = root
        src = root / "src"
        self.files = sorted(
            path for path in src.rglob("*.cs")
            if not {"bin", "obj"} & set(path.relative_to(src).parts)
        )
        self.code = {path: strip_comments(path.read_text(encoding="utf-8-sig")) for path in self.files}
        self.followed = [path for path in self.files if not set(EXCLUDED_FOLDERS) & set(path.parts)]

        self.declared = {}
        self.implementers = {}
        self.validators = {}

        for path in self.followed:
            code = self.code[path]

            for name in DECLARATION.findall(code):
                self.declared.setdefault(name, set()).add(path)

            for bases in TYPE_HEADER.findall(code):
                for name in IDENTIFIER.findall(bases):
                    self.implementers.setdefault(name, set()).add(path)

        # Validators live under Input/, which is not followed otherwise: an input's own type holds no logic.
        for path in self.files:
            for validated in VALIDATOR_OF.findall(self.code[path]):
                self.validators.setdefault(validated, set()).add(path)

        self.values = self._message_values()
        self.maps = self._status_maps()

    def _message_values(self):
        """Each message constant's text, which is what a status map is looked up by at run time."""
        values = {}

        for path in self.files:
            if path.parent.name != "Messages" or path.name.endswith("Map.cs"):
                continue

            for name, expression in CONSTANT.findall(self.code[path]):
                values[f"{path.stem}.{name}"] = "".join(STRING_LITERAL.findall(expression))

        return values

    def key(self, symbol):
        """What a map is keyed by for this symbol: its text, or the symbol itself for a library constant."""
        return self.values.get(symbol, symbol)

    def _status_maps(self):
        maps = {}
        base = {}

        for path in self.files:
            if path.parent.name == "Messages" and path.name.endswith("MessageMap.cs"):
                code = self.code[path]
                entries = {self.key(symbol): STATUSES[status] for symbol, status in MAP_ENTRY.findall(code)}
                maps[path.stem] = (entries, "DataAccessMessageMap.CombinedWith" in code)

                if path.stem == "DataAccessMessageMap":
                    base = entries

        return {name: {**base, **entries} if combined else entries for name, (entries, combined) in maps.items()}

    def reached(self, start):
        """The files the code in start reaches, start included."""
        seen = set()
        pending = [start]

        while pending:
            path = pending.pop()

            if path in seen:
                continue

            seen.add(path)
            code = self.code[path]

            for validated in VALIDATED.findall(code):
                pending.extend(self.validators.get(validated, ()))

            for name in set(IDENTIFIER.findall(code)):
                pending.extend(self.declared.get(name, ()))

                if name.startswith("I") and name in self.declared:
                    pending.extend(self.implementers.get(name, ()))

        return seen

    def handler(self, input_name):
        matches = [path for path in self.files
                   if path.name == f"{input_name}Handler.cs" and path.parent.name == "Handlers"]

        if len(matches) != 1:
            raise SystemExit(f"Expected one {input_name}Handler.cs under a Handlers folder, found {len(matches)}")

        return matches[0]


def actions(source):
    """Every controller member that dispatches through the mediator: (operation id, its source)."""
    controllers = source.root / "src/Presentation/ArturRios.Heimdall.WebApi/Controllers"

    for path in sorted(controllers.glob("*Controller.cs")):
        lines = path.read_text(encoding="utf-8-sig").replace("\r\n", "\n").split("\n")
        controller = path.stem[:-len("Controller")]
        index = 0

        while index < len(lines):
            if lines[index].startswith("    public "):
                end = next(line for line in range(index, len(lines)) if lines[line].rstrip() == "    }")
                member = "\n".join(lines[index:end + 1])

                if MEDIATOR_INPUT.search(member):
                    yield f"{controller}_{METHOD_NAME.search(member).group(1)}", strip_comments(member)

                index = end

            index += 1


def derive(root=REPO_ROOT):
    """{operation id: {status: [what can answer it]}}, for every operation whose handler can fail."""
    source = Source(root)
    catalog = {}

    for operation, member in actions(source):
        maps = [source.maps[name] for name in STATUS_MAP_ARGUMENT.findall(member)]

        if not maps:
            raise SystemExit(f"{operation}: no statusMap argument")

        handler = source.handler(MEDIATOR_INPUT.search(member).group(1))
        reached = source.reached(handler)
        reached_code = [source.code[path] for path in reached]
        symbols = {symbol for code in reached_code for symbol in MESSAGE_REFERENCE.findall(code)}

        handler_code = source.code[handler]
        if "IAsyncRepository<" in handler_code and re.search(r"\.WithErrors\(\w+\.Errors\)", handler_code):
            symbols.update(DATA_ACCESS)

        statuses = {}

        def add(status, entry):
            statuses.setdefault(str(status), set()).add(entry)

        for symbol in symbols:
            for status_map in maps:
                status = status_map.get(source.key(symbol))

                if status is not None and status >= 400:
                    add(status, symbol)

        if any(VALIDATED.search(code) for code in reached_code):
            add(400, VALIDATION)

        if any("PasswordHashGate.Shared." in code for code in reached_code):
            add(503, PASSWORD_HASH)

        if "429" in statuses and any(RETRY_ADVISOR in code for code in reached_code):
            add(429, RETRY_ADVISOR)

        if statuses:
            catalog[operation] = {status: sorted(entries) for status, entries in sorted(statuses.items())}

    return dict(sorted(catalog.items()))


def render(catalog):
    return json.dumps(catalog, indent=2, sort_keys=True) + "\n"


def write():
    CATALOG.write_text(render(derive()), encoding="utf-8", newline="\n")
    print(f"Wrote {CATALOG.relative_to(REPO_ROOT)}")


def check():
    """0 when the committed catalog is what the source derives, 1 otherwise."""
    current = CATALOG.read_text(encoding="utf-8") if CATALOG.exists() else ""

    if current == render(derive()):
        print(f"{CATALOG.relative_to(REPO_ROOT)} is up to date.")

        return 0

    print(f"{CATALOG.relative_to(REPO_ROOT)} is out of date. Regenerate it and commit the result:\n\n"
          "    python scripts/openapi.py\n")

    return 1


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true", help="fail if the committed catalog is out of date")

    if parser.parse_args().check:
        return check()

    write()

    return 0


if __name__ == "__main__":
    sys.exit(main())
