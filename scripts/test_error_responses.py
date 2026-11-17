"""Unit tests for error_responses.py, the derivation of each handler's error statuses.

Run from the repository root:  python -m unittest discover -s scripts -p "test_*.py"

Each case builds a small source tree in a temporary directory with the repository's layout -- a
controller, a handler, messages and their status map -- and reads what the derivation makes of it.
The last case holds the committed catalog to the repository's own source.
"""

import tempfile
import textwrap
import unittest
from pathlib import Path

import error_responses

MESSAGES = """\
namespace Sample.Messages;

public static class WidgetMessages
{
    public const string WidgetUpdated = "Widget updated.";
    public const string WidgetNotFound = "Widget not found.";
    public const string NameTaken =
        "A widget with " + "this name exists.";
    public const string Unmapped = "Not in any map.";
}
"""

MAP = """\
namespace Sample.Messages;

public static class WidgetMessageMap
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes =
        DataAccessMessageMap.CombinedWith(new Dictionary<string, int>
        {
            [WidgetMessages.WidgetUpdated] = HttpStatusCodes.Ok,
            [WidgetMessages.WidgetNotFound] = HttpStatusCodes.NotFound,
            [WidgetMessages.NameTaken] =
                HttpStatusCodes.Conflict
        });
}
"""

DATA_ACCESS_MAP = """\
public static class DataAccessMessageMap
{
    private static readonly IReadOnlyDictionary<string, int> s_statusCodes = new Dictionary<string, int>
    {
        [RelationalErrors.UniqueViolationMessage] = HttpStatusCodes.Conflict,
        [RelationalErrors.IntegrityViolationMessage] = HttpStatusCodes.Conflict,
        [RelationalErrors.ConcurrencyMessage] = HttpStatusCodes.Conflict,
        [RelationalErrors.TransientMessage] = HttpStatusCodes.ServiceUnavailable
    };
}
"""

CONTROLLER = """\
public class WidgetController(CommandMediator commandMediator) : Controller
{
    [HttpPut("widgets/{id:guid}")]
    public async Task<ActionResult<DataOutput<UpdateWidgetCommandOutput?>>> Update(
        Guid id, [FromBody] UpdateWidgetCommand command)
    {
        var result = await commandMediator
            .ExecuteCommandAsync<UpdateWidgetCommand, UpdateWidgetCommandOutput>(command);

        return result.ToActionResult(statusMap: WidgetMessageMap.StatusCodes);
    }
}
"""


def handler(body, parameters="IAsyncReadOnlyRepository<Widget, long> widgetReader"):
    return textwrap.dedent(f"""\
        public class UpdateWidgetCommandHandler(
            {parameters})
            : ICommandHandlerAsync<UpdateWidgetCommand, UpdateWidgetCommandOutput>
        {{
            public async Task<DataOutput<UpdateWidgetCommandOutput?>> HandleAsync(UpdateWidgetCommand command)
            {{
                var output = DataOutput<UpdateWidgetCommandOutput?>.New;
        {textwrap.indent(textwrap.dedent(body), "        ")}
                return output.WithMessage(WidgetMessages.WidgetUpdated);
            }}
        }}
        """)


class DeriveTests(unittest.TestCase):
    def derive(self, handler_source, extra=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            files = {
                "src/Application/Sample.Shared/Messages/WidgetMessages.cs": MESSAGES,
                "src/Application/Sample.Shared/Messages/WidgetMessageMap.cs": MAP,
                "src/Application/Sample.Shared/Messages/DataAccessMessageMap.cs": DATA_ACCESS_MAP,
                "src/Application/Sample.Command/Handlers/UpdateWidgetCommandHandler.cs": handler_source,
                "src/Presentation/ArturRios.Heimdall.WebApi/Controllers/WidgetController.cs": CONTROLLER,
                **(extra or {}),
            }

            for path, text in files.items():
                (root / path).parent.mkdir(parents=True, exist_ok=True)
                (root / path).write_text(text, encoding="utf-8")

            return error_responses.derive(root)

    def test_given_a_mapped_error_when_derived_then_it_is_listed_under_its_status(self):
        catalog = self.derive(handler("return output.WithError(WidgetMessages.WidgetNotFound);"))

        self.assertEqual({"404": ["WidgetMessages.WidgetNotFound"]}, catalog["Widget_Update"])

    def test_given_a_message_split_over_lines_when_derived_then_its_text_is_looked_up(self):
        catalog = self.derive(handler("return output.WithError(WidgetMessages.NameTaken);"))

        self.assertEqual({"409": ["WidgetMessages.NameTaken"]}, catalog["Widget_Update"])

    def test_given_only_success_and_unmapped_messages_when_derived_then_the_operation_is_left_out(self):
        catalog = self.derive(handler("if (command.Id == default) return output.WithError(WidgetMessages.Unmapped);"))

        self.assertNotIn("Widget_Update", catalog)

    def test_given_a_message_named_only_in_a_comment_when_derived_then_it_is_not_listed(self):
        catalog = self.derive(handler("// WidgetMessages.WidgetNotFound is answered elsewhere."))

        self.assertNotIn("Widget_Update", catalog)

    def test_given_a_service_that_fails_when_derived_then_its_errors_count_for_the_handler(self):
        service = {
            "src/Application/Sample.Command/Services/IWidgetLookup.cs": "public interface IWidgetLookup;\n",
            "src/Application/Sample.Command/Services/WidgetLookup.cs": textwrap.dedent("""\
                public class WidgetLookup : IWidgetLookup
                {
                    public string? Refuse() => WidgetMessages.WidgetNotFound;
                }
                """),
        }

        catalog = self.derive(handler("", parameters="IWidgetLookup lookup"), service)

        self.assertEqual({"404": ["WidgetMessages.WidgetNotFound"]}, catalog["Widget_Update"])

    def test_given_a_validator_when_derived_then_400_is_listed(self):
        validator = {
            "src/Application/Sample.Command/Input/Validation/UpdateWidgetCommandValidator.cs":
                "public class UpdateWidgetCommandValidator : AbstractValidator<UpdateWidgetCommand>;\n",
        }

        catalog = self.derive(handler("", parameters="IValidator<UpdateWidgetCommand> validator"), validator)

        self.assertEqual({"400": [error_responses.VALIDATION]}, catalog["Widget_Update"])

    def test_given_a_repository_write_whose_errors_are_returned_when_derived_then_its_failures_are_listed(self):
        body = """\
            var update = await widgetWriter.UpdateAsync(widget);
            if (!update.Success) return output.WithErrors(update.Errors);
            """

        catalog = self.derive(handler(body, parameters="IAsyncRepository<Widget, long> widgetWriter"))

        self.assertEqual(
            {
                "409": [
                    "RelationalErrors.ConcurrencyMessage",
                    "RelationalErrors.IntegrityViolationMessage",
                    "RelationalErrors.UniqueViolationMessage",
                ],
                "503": ["RelationalErrors.TransientMessage"],
            },
            catalog["Widget_Update"])

    def test_given_a_password_hash_reached_through_a_service_when_derived_then_the_gates_503_is_listed(self):
        service = {
            "src/Application/Sample.Command/Services/WidgetSecret.cs": textwrap.dedent("""\
                public static class WidgetSecret
                {
                    public static Task<bool> MatchesAsync(string text) =>
                        PasswordHashGate.Shared.TextMatchesAsync(text, "hash", "salt");
                }
                """),
        }

        catalog = self.derive(handler("await WidgetSecret.MatchesAsync(command.Name);"), service)

        self.assertEqual({"503": [error_responses.PASSWORD_HASH]}, catalog["Widget_Update"])

    def test_given_the_repository_when_derived_then_the_committed_catalog_is_current(self):
        self.assertEqual(
            error_responses.render(error_responses.derive()),
            error_responses.CATALOG.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
